using System.Text.Json;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Services;

/// <summary>
/// Finalizes one registry session into durable playback history. Provider usage
/// and request diagnostics are consumed exactly once after the registry removes
/// the session from its active lifecycle.
/// </summary>
public sealed class PlaybackSessionRecorder(
    ProviderUsageTracker usageTracker,
    PlaybackSessionStats playbackSessionStats,
    MetricsWriter metricsWriter)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Record(ActiveReadSessionSnapshot entry)
    {
        var failoverSaves = usageTracker.GetFailoverSaves(entry.Id);
        var segmentsByProvider = usageTracker.Snapshot(entry.Id);
        var bytesByProvider = usageTracker.SnapshotBytes(entry.Id);
        usageTracker.Clear(entry.Id);
        var totals = playbackSessionStats.Take(entry.Id);

        metricsWriter.RecordSession(BuildSession(
            entry,
            failoverSaves,
            segmentsByProvider,
            bytesByProvider,
            totals));
    }

    internal static ReadSession BuildSession(
        ActiveReadSessionSnapshot entry,
        long failoverSaves,
        IReadOnlyDictionary<string, long> segmentsByProvider,
        IReadOnlyDictionary<string, long> bytesByProvider,
        PlaybackSessionTotals? totals)
    {
        var bytesFetched = bytesByProvider.Values.Sum();
        return new ReadSession
        {
            Id = entry.Id,
            StartedAt = entry.StartedAt.ToUnixTimeMilliseconds(),
            EndedAt = entry.LastActivityAt.ToUnixTimeMilliseconds(),
            DurationMs = (int)Math.Min(
                int.MaxValue,
                (entry.LastActivityAt - entry.StartedAt).TotalMilliseconds),
            Path = entry.Path,
            FileSize = entry.FileSize,
            BytesServed = entry.BytesRead,
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
            MaxOffset = Math.Max(entry.MaxOffset, totals?.MaxOffset ?? 0),
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
                segmentsByProvider,
                bytesByProvider,
                totals),
            ErrorNote = totals?.ErrorNote,
        };
    }

    /// <summary>
    /// Merges provider usage with backup-attempt outcomes by stable provider id.
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
        providerIds.UnionWith(backups.Select(backup => backup.ProviderId));

        var stats = providerIds
            .Select(providerId =>
            {
                var backup = backups.FirstOrDefault(item =>
                    string.Equals(
                        item.ProviderId,
                        providerId,
                        StringComparison.OrdinalIgnoreCase));
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
            .OrderByDescending(stat => stat.Segments)
            .ThenByDescending(stat => stat.Rescued)
            .ThenBy(stat => stat.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(stats, JsonOptions);
    }

    private static int? ToInt(long? value) =>
        value is null ? null : (int)Math.Clamp(value.Value, 0, int.MaxValue);
}
