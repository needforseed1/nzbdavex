using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;
using static NzbWebDAV.Api.Controllers.GetPlaybackSessions.GetPlaybackSessionsResponse;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessions;

/// <summary>
/// Turns raw ReadSession rows into what the playback page shows. Kept free of
/// database and HTTP concerns so the grouping and issue rules can be tested
/// directly.
/// </summary>
public static class PlaybackHistory
{
    /// <summary>
    /// A pause longer than this is treated as a new play rather than a gap in the
    /// same one. Players routinely leave 15 s–minutes between range requests.
    /// </summary>
    public static readonly TimeSpan DefaultPlayGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Media scanners (Plex, ffprobe, Jellyfin) open every file in the library
    /// and read a few kilobytes of header. Those reads are indistinguishable
    /// from playback at the protocol level, and there are thousands of them, so
    /// they are classified by how little they took: real viewing pulls tens of
    /// megabytes within seconds. Duration is useless here — a slow scan can hold
    /// a file open for fifteen seconds and still read 300 KB.
    /// </summary>
    private const long ProbeMaxBytesServed = 8_000_000;

    private static readonly JsonSerializerOptions ProviderStatsJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static class Issue
    {
        /// <summary>
        /// The stream waited on usenet. Deliberately *not* raised for downstream
        /// waits: those are the client refusing more data because its buffer is
        /// full, which is what healthy playback looks like, not buffering.
        /// </summary>
        public const string Stalled = "stalled";

        /// <summary>
        /// Articles that could not be fetched were served as zeros. The only
        /// issue here that means the viewer got wrong data rather than late data.
        /// </summary>
        public const string Corrupted = "corrupted";

        /// <summary>
        /// A provider connection went silent mid-body and the segment had to be
        /// fetched again. Recovered — but a fault that heals leaves no other mark.
        /// </summary>
        public const string BodyStalled = "body-stalled";

        public const string Rescued = "rescued";
        public const string BackupUsed = "backup-used";
        public const string Rotated = "rotated";
        public const string BudgetExhausted = "budget-exhausted";
        public const string PoolStarved = "pool-starved";
        public const string PermitStarved = "permit-starved";
        public const string Aborted = "aborted";
        public const string TimedOut = "timeout";
        public const string Errored = "error";
    }

    /// <summary>
    /// Signals that could have affected the viewer. Recovery mechanics remain
    /// in the session diagnostics, but successful fallback, connection
    /// replacement, and internal queueing do not turn a play into a problem.
    /// </summary>
    private static readonly string[] PlaybackImpactIssues =
    [
        Issue.Corrupted,
        Issue.Stalled,
        Issue.TimedOut,
        Issue.Errored,
    ];

    public static SessionDto BuildSession(
        ReadSession row,
        IReadOnlyDictionary<string, UsenetProviderConfig.ConnectionDetails> providersById)
    {
        var counters = new CountersDto
        {
            UpstreamStalls = row.UpstreamStalls,
            MaxUpstreamStallMs = row.MaxUpstreamStallMs,
            TotalUpstreamStallMs = row.TotalUpstreamStallMs,
            HeadOfLineStalls = row.HeadOfLineStalls,
            TotalHeadOfLineStallMs = row.TotalHeadOfLineStallMs,
            DownstreamStalls = row.DownstreamStalls,
            MaxDownstreamStallMs = row.MaxDownstreamStallMs,
            TotalDownstreamStallMs = row.TotalDownstreamStallMs,
            FallbackRescues = row.FallbackRescues,
            ProviderRotations = row.ProviderRotations,
            FallbackBudgetExhaustions = row.FallbackBudgetExhaustions,
            CacheHits = row.CacheHits,
            CacheMisses = row.CacheMisses,
            ConnectionPermitWaits = row.ConnectionPermitWaits,
            MaxConnectionPermitWaitMs = row.MaxConnectionPermitWaitMs,
            ProviderPoolWaits = row.ProviderPoolWaits,
            MaxProviderPoolWaitMs = row.MaxProviderPoolWaitMs,
            FailoverSaves = row.FailoverSaves,
            ZeroFilledSegments = row.ZeroFilledSegments,
            ZeroFilledBytes = row.ZeroFilledBytes,
            BodyStallRecoveries = row.BodyStallRecoveries,
        };
        var providers = BuildProviders(row.ProviderStatsJson, providersById);
        var endReason = row.EndReason.ToString().ToLowerInvariant();

        return new SessionDto
        {
            Id = row.Id.ToString(),
            Path = row.Path,
            FileName = row.FileName,
            DavItemId = row.DavItemId?.ToString(),
            HistoryItemId = row.HistoryItemId?.ToString(),
            ClientIp = row.ClientIp,
            ClientUserAgent = row.ClientUserAgent,
            StartedAtUnix = row.StartedAt / 1000,
            EndedAtUnix = row.EndedAt / 1000,
            StartedAtMs = row.StartedAt,
            EndedAtMs = row.EndedAt,
            DurationMs = row.DurationMs,
            RequestCount = row.RequestCount,
            BytesServed = row.BytesServed,
            BytesFetched = row.BytesFetched,
            FileSize = row.FileSize,
            MaxOffset = row.MaxOffset,
            FirstByteMs = row.FirstByteMs,
            EndReason = endReason,
            ErrorNote = row.ErrorNote,
            HasDiagnostics = row.RequestCount > 0 || row.ProviderStatsJson is not null,
            Issues = DescribeIssues(counters, endReason, providers),
            Counters = counters,
            Providers = providers,
        };
    }

    public static List<ProviderDto> BuildProviders(
        string? providerStatsJson,
        IReadOnlyDictionary<string, UsenetProviderConfig.ConnectionDetails> providersById)
    {
        if (string.IsNullOrWhiteSpace(providerStatsJson)) return [];

        List<PlaybackProviderStat>? stats;
        try
        {
            stats = JsonSerializer.Deserialize<List<PlaybackProviderStat>>(
                providerStatsJson, ProviderStatsJsonOptions);
        }
        catch (JsonException)
        {
            return [];
        }
        if (stats is null) return [];

        return stats
            .Where(x => !string.IsNullOrWhiteSpace(x.ProviderId))
            .Select(x =>
            {
                providersById.TryGetValue(x.ProviderId, out var configured);
                return new ProviderDto
                {
                    ProviderId = x.ProviderId,
                    Host = configured?.Host ?? x.ProviderId,
                    Nickname = configured?.Nickname,
                    Segments = x.Segments,
                    Bytes = x.Bytes,
                    Attempts = x.Attempts,
                    Rescued = x.Rescued,
                    Missing = x.Missing,
                    Timeouts = x.Timeouts,
                    Errors = x.Errors,
                    IsBackup = x.IsBackup,
                };
            })
            .OrderByDescending(x => x.Segments)
            .ThenByDescending(x => x.Rescued)
            .ThenBy(x => x.Nickname ?? x.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<string> DescribeIssues(
        CountersDto counters,
        string endReason,
        IReadOnlyList<ProviderDto> providers)
    {
        var issues = new List<string>();
        // Substituted bytes first: everything else on this list is about the
        // stream being slow, this one is about it being wrong.
        if (counters.ZeroFilledSegments > 0) issues.Add(Issue.Corrupted);
        if (counters.BodyStallRecoveries > 0) issues.Add(Issue.BodyStalled);
        if (PlaybackIssueThresholds.StallsMatter(
                counters.UpstreamStalls, counters.MaxUpstreamStallMs))
            issues.Add(Issue.Stalled);
        if (counters.FallbackRescues > 0 || counters.FailoverSaves > 0 ||
            providers.Any(p => p.Rescued > 0))
            issues.Add(Issue.Rescued);
        if (providers.Any(p => p is { IsBackup: true, Segments: > 0 }))
            issues.Add(Issue.BackupUsed);
        if (counters.ProviderRotations > 0) issues.Add(Issue.Rotated);
        if (counters.FallbackBudgetExhaustions > 0) issues.Add(Issue.BudgetExhausted);
        if (PlaybackIssueThresholds.WaitsMatter(
                counters.ProviderPoolWaits, counters.MaxProviderPoolWaitMs))
            issues.Add(Issue.PoolStarved);
        if (PlaybackIssueThresholds.WaitsMatter(
                counters.ConnectionPermitWaits, counters.MaxConnectionPermitWaitMs))
            issues.Add(Issue.PermitStarved);

        switch (endReason)
        {
            case "aborted": issues.Add(Issue.Aborted); break;
            case "timeout": issues.Add(Issue.TimedOut); break;
            case "error": issues.Add(Issue.Errored); break;
        }

        return issues;
    }

    /// <summary>
    /// Groups sessions of the same file and client into plays, splitting whenever
    /// the viewer was away longer than <paramref name="gap"/>. Returns newest
    /// first. <paramref name="resolveContent"/> supplies display names the metrics
    /// database does not hold.
    /// </summary>
    public static List<PlayDto> GroupIntoPlays(
        IEnumerable<SessionDto> sessions,
        Func<SessionDto, PlaybackContentInfo?>? resolveContent = null,
        TimeSpan? gap = null)
    {
        var gapMs = (long)(gap ?? DefaultPlayGap).TotalMilliseconds;
        var ordered = sessions.OrderBy(x => x.StartedAtMs).ThenBy(x => x.Id).ToList();
        var openPlays = new Dictionary<string, List<SessionDto>>(StringComparer.Ordinal);
        var completed = new List<List<SessionDto>>();

        foreach (var session in ordered)
        {
            var key = BuildGroupKey(session);
            if (openPlays.TryGetValue(key, out var current))
            {
                var previousEnd = current.Max(x => x.EndedAtMs);
                if (session.StartedAtMs - previousEnd <= gapMs)
                {
                    current.Add(session);
                    continue;
                }
                completed.Add(current);
            }

            openPlays[key] = [session];
        }

        completed.AddRange(openPlays.Values);

        return completed
            .Select(group => BuildPlay(group, resolveContent))
            .OrderByDescending(x => x.StartedAtUnix)
            .ThenByDescending(x => x.EndedAtUnix)
            .ToList();
    }

    public static bool MatchesFilter(PlayDto play, string? filter) => filter?.ToLowerInvariant() switch
    {
        null or "" or "all" => true,
        "plays" => !play.IsProbe,
        "probes" or "scans" => play.IsProbe,
        "issues" => play.Issues.Any(x => PlaybackImpactIssues.Contains(x)),
        "failed" => play.EndReason is "timeout" or "error",
        _ => true,
    };

    /// <summary>
    /// A library scan rather than someone watching something. Anything with a
    /// viewer-impact signal stays a play no matter how little it served — a
    /// stream that died after 20 KB is the most interesting row on the page.
    /// </summary>
    private static bool IsProbe(long bytesServed, string endReason, IReadOnlyList<string> issues) =>
        bytesServed < ProbeMaxBytesServed
        && endReason is not ("timeout" or "error")
        && !issues.Any(x => PlaybackImpactIssues.Contains(x));

    private static string BuildGroupKey(SessionDto session) => string.Join(
        '\n',
        session.DavItemId ?? session.Path,
        session.ClientIp ?? "",
        session.ClientUserAgent ?? "");

    private static PlayDto BuildPlay(
        List<SessionDto> group,
        Func<SessionDto, PlaybackContentInfo?>? resolveContent)
    {
        var first = group[0];
        var last = group[^1];
        var startedMs = group.Min(x => x.StartedAtMs);
        var endedMs = group.Max(x => x.EndedAtMs);
        var watchedMs = group.Sum(x => (long)x.DurationMs);
        var bytesServed = group.Sum(x => x.BytesServed);
        var fileSize = group.Select(x => x.FileSize).FirstOrDefault(x => x is > 0);
        var maxOffset = group.Max(x => x.MaxOffset);
        var content = resolveContent?.Invoke(last) ?? resolveContent?.Invoke(first);
        var counters = MergeCounters(group.Select(x => x.Counters));
        var providers = MergeProviders(group.SelectMany(x => x.Providers));
        var issues = DescribeIssues(counters, last.EndReason, providers);

        return new PlayDto
        {
            Key = first.Id,
            Title = FirstNonEmpty(
                content?.Title,
                group.Select(x => x.FileName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                System.IO.Path.GetFileName(first.Path.TrimEnd('/')),
                first.Path),
            NzbName = content?.NzbName,
            Category = content?.Category,
            Path = first.Path,
            DavItemId = first.DavItemId,
            HistoryItemId = group.Select(x => x.HistoryItemId).FirstOrDefault(x => x is not null),
            ClientIp = first.ClientIp,
            ClientUserAgent = first.ClientUserAgent,
            StartedAtUnix = startedMs / 1000,
            EndedAtUnix = endedMs / 1000,
            WatchedMs = watchedMs,
            SpanMs = Math.Max(0, endedMs - startedMs),
            FileSize = fileSize,
            MaxOffset = maxOffset,
            ReachedPct = fileSize is > 0
                ? Math.Min(100d, maxOffset * 100d / fileSize.Value)
                : null,
            BytesServed = bytesServed,
            BytesFetched = group.Sum(x => x.BytesFetched),
            AvgBytesPerSecond = watchedMs > 0
                ? (long)(bytesServed * 1000d / watchedMs)
                : 0,
            SourceBytesPerSecond = watchedMs > 0
                ? (long)(group.Sum(x => x.BytesFetched) * 1000d / watchedMs)
                : 0,
            // Startup latency: what the viewer waited before anything played.
            // The group is ordered oldest first, so this is the first session
            // that measured a first byte — not the smallest measurement, which
            // is a mid-play seek off an already-warm stream.
            FirstByteMs = group.FirstOrDefault(x => x.FirstByteMs.HasValue)?.FirstByteMs,
            // The last session decides how the play ended; earlier ones are seeks.
            EndReason = last.EndReason,
            ErrorNote = group.Select(x => x.ErrorNote).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            HasDiagnostics = group.Any(x => x.HasDiagnostics),
            IsProbe = IsProbe(bytesServed, last.EndReason, issues),
            Issues = issues,
            Counters = counters,
            Providers = providers,
            Sessions = group.OrderByDescending(x => x.StartedAtMs).ToList(),
        };
    }

    private static CountersDto MergeCounters(IEnumerable<CountersDto> counters)
    {
        var all = counters.ToList();
        return new CountersDto
        {
            UpstreamStalls = all.Sum(x => x.UpstreamStalls),
            MaxUpstreamStallMs = all.Count == 0 ? 0 : all.Max(x => x.MaxUpstreamStallMs),
            TotalUpstreamStallMs = all.Sum(x => x.TotalUpstreamStallMs),
            HeadOfLineStalls = all.Sum(x => x.HeadOfLineStalls),
            TotalHeadOfLineStallMs = all.Sum(x => x.TotalHeadOfLineStallMs),
            DownstreamStalls = all.Sum(x => x.DownstreamStalls),
            MaxDownstreamStallMs = all.Count == 0 ? 0 : all.Max(x => x.MaxDownstreamStallMs),
            TotalDownstreamStallMs = all.Sum(x => x.TotalDownstreamStallMs),
            FallbackRescues = all.Sum(x => x.FallbackRescues),
            ProviderRotations = all.Sum(x => x.ProviderRotations),
            FallbackBudgetExhaustions = all.Sum(x => x.FallbackBudgetExhaustions),
            CacheHits = all.Sum(x => x.CacheHits),
            CacheMisses = all.Sum(x => x.CacheMisses),
            ConnectionPermitWaits = all.Sum(x => x.ConnectionPermitWaits),
            MaxConnectionPermitWaitMs = all.Count == 0 ? 0 : all.Max(x => x.MaxConnectionPermitWaitMs),
            ProviderPoolWaits = all.Sum(x => x.ProviderPoolWaits),
            MaxProviderPoolWaitMs = all.Count == 0 ? 0 : all.Max(x => x.MaxProviderPoolWaitMs),
            FailoverSaves = all.Sum(x => x.FailoverSaves),
            ZeroFilledSegments = all.Sum(x => x.ZeroFilledSegments),
            ZeroFilledBytes = all.Sum(x => x.ZeroFilledBytes),
            BodyStallRecoveries = all.Sum(x => x.BodyStallRecoveries),
        };
    }

    private static List<ProviderDto> MergeProviders(IEnumerable<ProviderDto> providers) => providers
        .GroupBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase)
        .Select(group => new ProviderDto
        {
            ProviderId = group.Key,
            Host = group.First().Host,
            Nickname = group.Select(x => x.Nickname).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            Segments = group.Sum(x => x.Segments),
            Bytes = group.Sum(x => x.Bytes),
            Attempts = group.Sum(x => x.Attempts),
            Rescued = group.Sum(x => x.Rescued),
            Missing = group.Sum(x => x.Missing),
            Timeouts = group.Sum(x => x.Timeouts),
            Errors = group.Sum(x => x.Errors),
            IsBackup = group.Any(x => x.IsBackup),
        })
        .OrderByDescending(x => x.Segments)
        .ThenByDescending(x => x.Rescued)
        .ThenBy(x => x.Nickname ?? x.Host, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "unknown";
}

public sealed record PlaybackContentInfo(string? Title, string? NzbName, string? Category);
