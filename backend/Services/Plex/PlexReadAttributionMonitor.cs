using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.Plex;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services.Plex;

public sealed record PlexMonitorStatus(
    bool Enabled,
    bool Connected,
    long? LastSuccessfulPollAt,
    string? LastError,
    string? ServerName,
    string? ServerVersion)
{
    public bool? ActivitiesConnected { get; init; }
    public string? ActivitiesError { get; init; }
}

/// <summary>
/// Source/purpose metadata attached to one existing NzbDAVex read. This is not
/// Plex history: it exists only because an rclone read overlapped a current
/// Plex observation.
/// </summary>
public sealed record PlexReadAttribution
{
    public required string Purpose { get; init; }
    public required string Confidence { get; init; }
    public string? Product { get; init; }
    public string? Player { get; init; }
    public string? Platform { get; init; }
    public string? RatingKey { get; init; }
    public string? Detail { get; init; }
    public bool IsTranscode { get; init; }
}

/// <summary>
/// Polls only current Plex state and retains a short in-memory sample window so
/// a completed rclone read can be annotated at recording time. No standalone
/// Plex sessions, watch progress, users, or network addresses are persisted.
/// </summary>
public sealed class PlexReadAttributionMonitor(
    ConfigManager configManager,
    PlexClient client,
    PlexPathResolver pathResolver) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SampleRetention = TimeSpan.FromMinutes(30);
    private const long MatchGraceMs = 10_000;

    private readonly object _gate = new();
    private readonly List<AttributionSample> _samples = [];
    private readonly Dictionary<string, Guid> _pathCache =
        new(StringComparer.Ordinal);
    private PlexMonitorStatus _status = new(false, false, null, null, null, null);
    private string? _endpointKey;
    private PlexServerInfo _server = new(null, null, null);
    private bool _loggedSessionFailure;
    private bool _loggedActivityFailure;
    private bool _loggedNotificationFailure;
    private int _consecutiveFailures;
    private bool _activityEndpointUnsupported;
    private long _nextActivityAttemptAt;

    public PlexMonitorStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public PlexReadAttribution? Match(
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        Guid? davItemId,
        string? clientUserAgent) =>
        Match(
            startedAt.ToUnixTimeMilliseconds(),
            endedAt.ToUnixTimeMilliseconds(),
            davItemId,
            clientUserAgent);

    internal PlexReadAttribution? Match(
        long startedAt,
        long endedAt,
        Guid? davItemId,
        string? clientUserAgent)
    {
        if (!IsRclone(clientUserAgent)) return null;

        List<AttributionSample> overlapping;
        lock (_gate)
        {
            overlapping = _samples.Where(sample =>
                    sample.At >= startedAt - MatchGraceMs
                    && sample.At <= endedAt + MatchGraceMs)
                .ToList();
        }
        if (overlapping.Count == 0) return null;

        if (davItemId is { } exactId)
        {
            var exact = overlapping.Where(sample =>
                    sample.Source == "session" && sample.DavItemId == exactId)
                .ToList();
            var exactMatch = BuildUniqueAttribution(exact, "exact-path");
            if (exactMatch is not null) return exactMatch;
        }

        // A resolved Plex session for another item is evidence against a match,
        // not a reason to use timing. Background activities and sessions whose
        // media path could not be resolved remain eligible for a clearly marked
        // time-only attribution.
        var timeOnly = overlapping.Where(sample =>
                sample.DavItemId is null &&
                (sample.Source == "activity" || sample.Purpose == "playback"))
            .ToList();
        return BuildUniqueAttribution(timeOnly, "time-only");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            PollLoopAsync(stoppingToken),
            NotificationLoopAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task PollLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(NextPollDelay(), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }

    private async Task NotificationLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var endpoint = CurrentEndpoint();
                if (endpoint is null)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var connectedAt = DateTimeOffset.UtcNow;
                using var endpointCts =
                    CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    var listenTask = client.ListenForActivityNotificationsAsync(
                        endpoint.Value.BaseUrl,
                        endpoint.Value.Token,
                        activities =>
                        {
                            PlexServerInfo server;
                            lock (_gate) server = _server;
                            RecordActivities(
                                activities,
                                server,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        },
                        endpointCts.Token);

                    while (!listenTask.IsCompleted &&
                           !stoppingToken.IsCancellationRequested)
                    {
                        await Task.WhenAny(
                                listenTask,
                                Task.Delay(PollInterval, stoppingToken))
                            .ConfigureAwait(false);
                        if (CurrentEndpoint()?.Key == endpoint.Value.Key) continue;
                        endpointCts.Cancel();
                    }
                    await listenTask.ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromSeconds(30))
                        _loggedNotificationFailure = false;
                }
                catch (OperationCanceledException) when (
                    stoppingToken.IsCancellationRequested ||
                    endpointCts.IsCancellationRequested)
                {
                    // Shutdown or a live configuration change.
                }
                catch (Exception e)
                {
                    if (!_loggedNotificationFailure)
                    {
                        Log.Warning(
                            "Plex activity notifications are unavailable; polling remains active: {Message}",
                            e.Message);
                        _loggedNotificationFailure = true;
                    }
                }

                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }

    private (string BaseUrl, string Token, string Key)? CurrentEndpoint()
    {
        if (!configManager.IsPlexEnabled()) return null;
        var baseUrl = configManager.GetPlexBaseUrl();
        var token = configManager.GetPlexToken();
        return baseUrl is null || token is null
            ? null
            : (baseUrl, token, $"{baseUrl}\n{token}");
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var enabled = configManager.IsPlexEnabled();
        var baseUrl = configManager.GetPlexBaseUrl();
        var token = configManager.GetPlexToken();

        if (!enabled)
        {
            lock (_gate)
            {
                ResetEndpoint(clearSamples: true);
                _status = new(false, false, _status.LastSuccessfulPollAt, null, null, null);
            }
            return;
        }

        if (baseUrl is null || token is null)
        {
            lock (_gate)
            {
                ResetEndpoint(clearSamples: true);
                _status = new(
                    true,
                    false,
                    _status.LastSuccessfulPollAt,
                    baseUrl is null
                        ? "Plex server URL is required."
                        : "Plex token is required.",
                    null,
                    null);
            }
            return;
        }

        var endpointKey = $"{baseUrl}\n{token}";
        try
        {
            if (_endpointKey != endpointKey)
            {
                var server = await client.GetServerInfoAsync(
                        baseUrl, token, cancellationToken)
                    .ConfigureAwait(false);
                lock (_gate)
                {
                    ResetEndpoint(clearSamples: true);
                    _endpointKey = endpointKey;
                    _server = server;
                }
            }

            var sessions = await client.GetSessionsAsync(
                    baseUrl, token, cancellationToken)
                .ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RecordSessions(sessions, _server, now);
            lock (_gate)
            {
                _status = _status with
                {
                    Enabled = true,
                    Connected = true,
                    LastSuccessfulPollAt = now,
                    LastError = null,
                    ServerName = _server.Name,
                    ServerVersion = _server.Version,
                };
                _loggedSessionFailure = false;
                _consecutiveFailures = 0;
            }

            await PollActivitiesAsync(baseUrl, token, _server, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            lock (_gate)
                _status = _status with
                {
                    Enabled = true,
                    Connected = false,
                    LastError = e.Message,
                };
            if (!_loggedSessionFailure)
            {
                Log.Warning("Plex read attribution is unavailable: {Message}", e.Message);
                _loggedSessionFailure = true;
            }
            _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 4);
        }
    }

    private async Task PollActivitiesAsync(
        string baseUrl,
        string token,
        PlexServerInfo server,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_activityEndpointUnsupported || now < _nextActivityAttemptAt) return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var activities = await client.GetActivitiesAsync(
                    baseUrl, token, timeout.Token)
                .ConfigureAwait(false);
            now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            RecordActivities(activities, server, now);
            lock (_gate)
            {
                _status = _status with
                {
                    ActivitiesConnected = true,
                    ActivitiesError = null,
                };
                _loggedActivityFailure = false;
                _nextActivityAttemptAt = 0;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordActivityFailure("Plex activity polling timed out.");
        }
        catch (HttpRequestException e) when (
            e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            lock (_gate)
            {
                _activityEndpointUnsupported = true;
                _status = _status with
                {
                    ActivitiesConnected = false,
                    ActivitiesError =
                        "This Plex server does not expose current activities.",
                };
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RecordActivityFailure(e.Message);
        }
    }

    internal void RecordSessions(
        IReadOnlyList<PlexSessionObservation> sessions,
        PlexServerInfo server,
        long now)
    {
        var samples = sessions.Select(session => new AttributionSample
        {
            At = now,
            CorrelationKey = string.Join('|',
                "session",
                server.MachineIdentifier ?? "unknown-server",
                session.SessionId ?? session.SessionKey,
                session.PlayerMachineIdentifier ?? "unknown-player"),
            Source = "session",
            Purpose = ClassifySession(session),
            DavItemId = ResolvePath(session.MediaPartPath),
            Product = CombineVersion(session.Product, session.PlayerVersion),
            Player = session.PlayerTitle,
            Platform = CombineVersion(session.Platform, session.PlatformVersion),
            RatingKey = session.RatingKey,
            Detail = session.Title,
            IsTranscode = session.IsTranscode,
        });
        AddSamples(samples, now);
    }

    internal void RecordActivities(
        IReadOnlyList<PlexActivityObservation> activities,
        PlexServerInfo server,
        long now)
    {
        var samples = activities.Select(activity =>
            {
                var purpose = ClassifyActivity(activity);
                return purpose is null
                    ? null
                    : new AttributionSample
                    {
                        At = now,
                        CorrelationKey = string.Join('|',
                            "activity",
                            server.MachineIdentifier ?? "unknown-server",
                            activity.Key),
                        Source = "activity",
                        Purpose = purpose,
                        Detail = CombineDetail(activity.Title, activity.Subtitle),
                    };
            })
            .Where(sample => sample is not null)
            .Cast<AttributionSample>();
        AddSamples(samples, now);
    }

    internal static string ClassifySession(PlexSessionObservation session) =>
        session.State.Trim().ToLowerInvariant() switch
        {
            "playing" => "playback",
            "paused" => "paused",
            "buffering" => "prebuffering",
            "stopped" => "stopped",
            _ when session.IsTranscode => "transcode",
            _ => "plex-session",
        };

    internal static string? ClassifyActivity(PlexActivityObservation activity)
    {
        var type = activity.Type?.Trim().ToLowerInvariant() ?? "";
        var text = string.Join(' ', activity.Title, activity.Subtitle)
            .ToLowerInvariant();
        var combined = $"{type} {text}";

        if (combined.Contains("deepmediaanalysis")
            || combined.Contains("deep media analysis"))
            return "deep-media-analysis";
        if (combined.Contains("intro")) return "intro-detection";
        if (combined.Contains("credit") || combined.Contains("outro"))
            return "credits-detection";
        if (combined.Contains("thumbnail")
            || combined.Contains("preview")
            || combined.Contains("bif"))
            return "thumbnail-generation";
        if (combined.Contains("chapter")) return "chapter-generation";
        if (combined.Contains("loudness")) return "loudness-analysis";
        if (combined.Contains("sonic")) return "sonic-analysis";
        if (combined.Contains("fingerprint")) return "fingerprinting";
        if (combined.Contains("analy")) return "media-analysis";

        var libraryType = type.StartsWith("library.", StringComparison.Ordinal)
                          && (type.Contains("update")
                              || type.Contains("scan")
                              || type.Contains("refresh"));
        var libraryText = (text.Contains("scan")
                           || text.Contains("refresh")
                           || text.Contains("update"))
                          && (text.Contains("library")
                              || text.Contains("movie")
                              || text.Contains("show")
                              || text.Contains("music")
                              || text.Contains("photo"));
        return libraryType || libraryText ? "library-scan" : null;
    }

    private Guid? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (_pathCache.TryGetValue(path, out var cached)) return cached;
        var resolved = pathResolver.ResolveDavItemId(path);
        // Cache only identities we proved. A path can become resolvable after
        // Plex first exposes it, so a transient miss must not last forever.
        if (resolved is { } id) _pathCache[path] = id;
        return resolved;
    }

    private void AddSamples(IEnumerable<AttributionSample> samples, long now)
    {
        lock (_gate)
        {
            _samples.AddRange(samples);
            var cutoff = now - (long)SampleRetention.TotalMilliseconds;
            _samples.RemoveAll(sample => sample.At < cutoff);
        }
    }

    private static PlexReadAttribution? BuildUniqueAttribution(
        IReadOnlyList<AttributionSample> samples,
        string confidence)
    {
        var keys = samples.Select(sample => sample.CorrelationKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (keys.Count != 1) return null;

        // A brief "playing" observation must not dominate a long paused read.
        // Poll-count is an approximation of how much of this NzbDAVex read
        // overlapped each Plex state; on a tie, the most recent state wins.
        var purpose = samples
            .GroupBy(sample => sample.Purpose, StringComparer.Ordinal)
            .Select(group => new
            {
                Purpose = group.Key,
                Count = group.Count(),
                LastObservedAt = group.Max(sample => sample.At),
            })
            .OrderByDescending(group => group.Count)
            .ThenByDescending(group => group.LastObservedAt)
            .First()
            .Purpose;
        var representative = samples
            .Where(sample => sample.Purpose == purpose)
            .OrderByDescending(sample => sample.At)
            .First();
        return new PlexReadAttribution
        {
            Purpose = representative.Purpose,
            Confidence = confidence,
            Product = representative.Product,
            Player = representative.Player,
            Platform = representative.Platform,
            RatingKey = representative.RatingKey,
            Detail = representative.Detail,
            IsTranscode = representative.IsTranscode,
        };
    }

    private void RecordActivityFailure(string message)
    {
        lock (_gate)
        {
            _nextActivityAttemptAt =
                DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeMilliseconds();
            _status = _status with
            {
                ActivitiesConnected = false,
                ActivitiesError = message,
            };
        }
        if (_loggedActivityFailure) return;
        Log.Warning("Plex activity attribution is unavailable: {Message}", message);
        _loggedActivityFailure = true;
    }

    private void ResetEndpoint(bool clearSamples)
    {
        _endpointKey = null;
        _server = new(null, null, null);
        _pathCache.Clear();
        _activityEndpointUnsupported = false;
        _loggedSessionFailure = false;
        _loggedActivityFailure = false;
        _loggedNotificationFailure = false;
        _consecutiveFailures = 0;
        _nextActivityAttemptAt = 0;
        if (clearSamples) _samples.Clear();
    }

    private TimeSpan NextPollDelay() => _consecutiveFailures switch
    {
        0 or 1 => PollInterval,
        2 => TimeSpan.FromSeconds(4),
        3 => TimeSpan.FromSeconds(8),
        _ => TimeSpan.FromSeconds(10),
    };

    private static bool IsRclone(string? userAgent) =>
        userAgent?.Contains("rclone", StringComparison.OrdinalIgnoreCase) == true;

    private static string? CombineVersion(string? name, string? version) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";

    private static string? CombineDetail(string? title, string? subtitle)
    {
        var values = new[] { title, subtitle }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var detail = string.Join(" · ", values);
        return detail.Length == 0 ? null : detail;
    }

    private sealed record AttributionSample
    {
        public required long At { get; init; }
        public required string CorrelationKey { get; init; }
        public required string Source { get; init; }
        public required string Purpose { get; init; }
        public Guid? DavItemId { get; init; }
        public string? Product { get; init; }
        public string? Player { get; init; }
        public string? Platform { get; init; }
        public string? RatingKey { get; init; }
        public string? Detail { get; init; }
        public bool IsTranscode { get; init; }
    }
}
