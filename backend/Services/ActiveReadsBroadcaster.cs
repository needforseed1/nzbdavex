using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Ticks once per second to publish the current set of active WebDAV read
/// sessions plus their per-backbone segment counts over the websocket. When no
/// sessions are active, the loop is mostly idle (just a sleep + a Count check).
/// Sends nothing when nothing has changed since the last broadcast.
/// </summary>
public class ActiveReadsBroadcaster(
    ActiveReadRegistry registry,
    ProviderUsageTracker usageTracker,
    WebsocketManager websocketManager,
    MetricsWriter metricsWriter,
    ConfigManager configManager,
    PlaybackSessionStats playbackSessionStats
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatsStaleAfter = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string? _lastPayload;
    private bool _wasEmpty = true;
    // Byte counters are cumulative, so a live rate needs the previous tick.
    private readonly Dictionary<Guid, (long Bytes, DateTimeOffset At)> _lastBytes = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await BroadcastTickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Debug(e, "ActiveReadsBroadcaster tick failed");
            }
        }
    }

    private async Task BroadcastTickAsync()
    {
        // Prune sessions that haven't been touched in the activity window first,
        // so their counters don't leak in the tracker. Each pruned entry becomes
        // a terminal ReadSession row so the dashboard can show historical reads.
        var pruned = registry.PruneExpired();
        foreach (var entry in pruned)
        {
            PersistSession(entry);
            _lastBytes.Remove(entry.Id);
        }

        var entries = registry.Snapshot();
        // Accumulators whose session never reached the prune path would otherwise
        // linger for the process lifetime. Never age out a request that is still
        // registered: a quiet ten-minute stretch in a long movie is not a stale
        // diagnostic session.
        playbackSessionStats.DropStale(
            StatsStaleAfter,
            entries.Select(entry => entry.Id).ToHashSet());
        var now = DateTimeOffset.UtcNow;

        // Common case: nothing active, nothing was active. Skip serialization entirely.
        if (entries.Count == 0 && _wasEmpty) return;

        var usage = usageTracker.SnapshotMany(entries.Select(e => e.Id));
        var providersByIdentity = configManager.GetUsenetProviderConfig().Providers
            .ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var snapshot = new
        {
            reads = entries.Select(e =>
            {
                var bytesRead = Interlocked.Read(ref e.BytesRead);
                // One snapshot per read: three separate Peeks could disagree.
                var live = playbackSessionStats.Peek(e.Id);
                return new
                {
                id = e.Id,
                fileName = e.FileName,
                path = e.Path,
                startedAt = e.StartedAt.ToUnixTimeMilliseconds(),
                lastActivityAt = e.LastActivityAt.ToUnixTimeMilliseconds(),
                bytesRead,
                currentOffset = Interlocked.Read(ref e.CurrentOffset),
                fileSize = e.FileSize,
                bytesPerSecond = RateSince(e.Id, bytesRead, now),
                // Live totals so the dashboard can say whether this read is
                // waiting on usenet or simply being paced by its player.
                upstreamStalls = live?.UpstreamStalls ?? 0,
                totalUpstreamStallMs = live?.TotalUpstreamStallMs ?? 0,
                upstreamWaitsInProgress = live?.ActiveUpstreamWaits ?? 0,
                downstreamStalls = live?.DownstreamStalls ?? 0,
                // Corruption in progress, not a delay: worth its own signal on
                // the live panel rather than waiting for the session to end.
                zeroFilledSegments = live?.ZeroFilledSegments ?? 0,
                bodyStallRecoveries = live?.BodyStallRecoveries ?? 0,
                providers = (usage.GetValueOrDefault(e.Id) ?? new Dictionary<string, long>())
                    .Select(kv => new
                    {
                        host = providersByIdentity.GetValueOrDefault(kv.Key)?.Host ?? kv.Key,
                        nickname = providersByIdentity.GetValueOrDefault(kv.Key)?.Nickname,
                        segments = kv.Value,
                    })
                    .OrderByDescending(p => p.segments)
                    .ToList()
                };
            }).ToList()
        };

        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (payload == _lastPayload) return;
        _lastPayload = payload;
        _wasEmpty = entries.Count == 0;
        await websocketManager.SendMessage(WebsocketTopic.ActiveReads, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// Bytes per second since the previous tick. Returns 0 for a read seen for
    /// the first time, which is honest: no interval has been observed yet.
    /// </summary>
    private long RateSince(Guid id, long bytes, DateTimeOffset now)
    {
        if (!_lastBytes.TryGetValue(id, out var previous))
        {
            _lastBytes[id] = (bytes, now);
            return 0;
        }

        var elapsed = (now - previous.At).TotalSeconds;
        _lastBytes[id] = (bytes, now);
        if (elapsed <= 0) return 0;
        return (long)Math.Max(0, (bytes - previous.Bytes) / elapsed);
    }

    /// <summary>
    /// Turn one finished read into its terminal ReadSession row, folding in the
    /// playback diagnostics accumulated across every range request it served.
    /// </summary>
    private void PersistSession(ActiveReadRegistry.Entry entry)
    {
        var failoverSaves = usageTracker.GetFailoverSaves(entry.Id);
        var segmentsByProvider = usageTracker.Snapshot(entry.Id);
        var bytesByProvider = usageTracker.SnapshotBytes(entry.Id);
        var bytesFetched = bytesByProvider.Values.Sum();
        usageTracker.Clear(entry.Id);
        var totals = playbackSessionStats.Take(entry.Id);

        metricsWriter.RecordSession(new ReadSession
        {
            Id = entry.Id,
            StartedAt = entry.StartedAt.ToUnixTimeMilliseconds(),
            EndedAt = entry.LastActivityAt.ToUnixTimeMilliseconds(),
            DurationMs = (int)Math.Min(int.MaxValue,
                (entry.LastActivityAt - entry.StartedAt).TotalMilliseconds),
            Path = entry.Path,
            FileSize = entry.FileSize,
            BytesServed = Interlocked.Read(ref entry.BytesRead),
            BytesFetched = bytesFetched,
            FailoverSaves = (int)Math.Min(int.MaxValue, failoverSaves),
            ClientUserAgent = entry.ClientUserAgent,
            ClientIp = entry.ClientIp,
            EndReason = entry.EndReason,
            FileName = string.IsNullOrWhiteSpace(entry.FileName) ? null : entry.FileName,
            DavItemId = entry.DavItemId,
            HistoryItemId = entry.HistoryItemId,
            RequestCount = totals?.RequestCount ?? 0,
            FirstByteMs = ToInt(totals?.FirstByteMs),
            MaxOffset = Math.Max(
                Interlocked.Read(ref entry.MaxOffset),
                totals?.MaxOffset ?? 0),
            UpstreamStalls = totals?.UpstreamStalls ?? 0,
            MaxUpstreamStallMs = ToInt(totals?.MaxUpstreamStallMs) ?? 0,
            TotalUpstreamStallMs = totals?.TotalUpstreamStallMs ?? 0,
            HeadOfLineStalls = totals?.HeadOfLineStalls ?? 0,
            TotalHeadOfLineStallMs = totals?.TotalHeadOfLineStallMs ?? 0,
            DownstreamStalls = totals?.DownstreamStalls ?? 0,
            MaxDownstreamStallMs = ToInt(totals?.MaxDownstreamStallMs) ?? 0,
            TotalDownstreamStallMs = totals?.TotalDownstreamStallMs ?? 0,
            FallbackRescues = totals?.FallbackRescues ?? 0,
            ProviderRotations = totals?.ProviderRotations ?? 0,
            FallbackBudgetExhaustions = totals?.FallbackBudgetExhaustions ?? 0,
            CacheHits = totals?.CacheHits ?? 0,
            CacheMisses = totals?.CacheMisses ?? 0,
            ConnectionPermitWaits = totals?.ConnectionPermitWaits ?? 0,
            MaxConnectionPermitWaitMs = ToInt(totals?.MaxConnectionPermitWaitMs) ?? 0,
            ProviderPoolWaits = totals?.ProviderPoolWaits ?? 0,
            MaxProviderPoolWaitMs = ToInt(totals?.MaxProviderPoolWaitMs) ?? 0,
            ZeroFilledSegments = totals?.ZeroFilledSegments ?? 0,
            ZeroFilledBytes = totals?.ZeroFilledBytes ?? 0,
            BodyStallRecoveries = totals?.BodyStallRecoveries ?? 0,
            ProviderStatsJson = BuildProviderStatsJson(
                segmentsByProvider, bytesByProvider, totals),
            ErrorNote = totals?.ErrorNote,
        });
    }

    /// <summary>
    /// Merges the two sources of provider truth: the usage tracker knows how many
    /// segments and bytes each provider served, the playback diagnostics know how
    /// the backup providers behaved when they were called on. Keyed by provider id
    /// so a backup that also served segments stays a single row.
    /// </summary>
    internal static string? BuildProviderStatsJson(
        IReadOnlyDictionary<string, long> segmentsByProvider,
        IReadOnlyDictionary<string, long> bytesByProvider,
        PlaybackSessionTotals? totals)
    {
        var backups = totals?.BackupProviders ?? [];
        if (segmentsByProvider.Count == 0 && bytesByProvider.Count == 0 && backups.Count == 0)
            return null;

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        providerIds.UnionWith(segmentsByProvider.Keys);
        providerIds.UnionWith(bytesByProvider.Keys);
        providerIds.UnionWith(backups.Select(x => x.ProviderId));

        var stats = providerIds
            .Select(providerId =>
            {
                var backup = backups.FirstOrDefault(x =>
                    string.Equals(x.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
                return new PlaybackProviderStat(
                    providerId,
                    segmentsByProvider.GetValueOrDefault(providerId),
                    bytesByProvider.GetValueOrDefault(providerId),
                    backup?.Attempts ?? 0,
                    backup?.Rescued ?? 0,
                    backup?.Missing ?? 0,
                    backup?.Timeouts ?? 0,
                    backup?.Errors ?? 0,
                    backup is not null);
            })
            .OrderByDescending(x => x.Segments)
            .ThenByDescending(x => x.Rescued)
            .ThenBy(x => x.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(stats, JsonOptions);
    }

    private static int? ToInt(long? value) =>
        value is null ? null : (int)Math.Clamp(value.Value, 0, int.MaxValue);

    /// <summary>
    /// On shutdown the tick loop stops before the activity window expires, so
    /// anything still streaming would never reach PersistSession. Flush it.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in registry.DrainAll()) PersistSession(entry);
        }
        catch (Exception e)
        {
            Log.Debug(e, "ActiveReadsBroadcaster shutdown flush failed");
        }
    }
}
