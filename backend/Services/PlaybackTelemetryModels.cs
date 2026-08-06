namespace NzbWebDAV.Services;

/// <summary>
/// Durable per-provider breakdown of one playback session: how many segments and
/// bytes each provider served, plus the backup-attempt outcomes. Stored as JSON on
/// the session row because raw SegmentFetches expire long before the session does.
/// Provider ids only — hosts and nicknames are resolved from live config at render
/// time so a rename retroactively fixes old rows.
/// </summary>
public sealed record PlaybackProviderStat(
    string ProviderId,
    long Segments,
    long Bytes,
    long Attempts,
    long Rescued,
    long Missing,
    long Timeouts,
    long Errors,
    bool IsBackup);

/// <summary>Per-backup-provider activity observed during one request or session.</summary>
public sealed record PlaybackBackupProviderStat(
    string ProviderId,
    string Host,
    long Attempts,
    long Rescued,
    long Missing,
    long Timeouts,
    long Errors);

/// <summary>
/// One wall-clock interval where at least one upstream read belonging to the
/// session was waiting. Concurrent waits are merged before these leave memory.
/// </summary>
public sealed record PlaybackWaitWindow(long StartedAtMs, long EndedAtMs);

/// <summary>
/// What a single finished HTTP playback request contributes to its session.
/// Stalls are absent by design: they are reported through
/// <see cref="PlaybackSessionStats.RecordStall"/> as they happen, so a live
/// view does not have to wait for the request to end.
/// </summary>
public sealed record PlaybackRequestDelta(
    /// When the request began. Requests complete out of order, so this is what
    /// identifies the one whose first byte was the viewer's startup wait.
    DateTimeOffset RequestStartedAt,
    long? FirstByteMs,
    long MaxOffset,
    int FallbackRescues,
    int ProviderRotations,
    int FallbackBudgetExhaustions,
    int CacheHits,
    int CacheMisses,
    int ConnectionPermitWaits,
    long MaxConnectionPermitWaitMs,
    int ProviderPoolWaits,
    long MaxProviderPoolWaitMs,
    int ZeroFilledSegments,
    long ZeroFilledBytes,
    int BodyStallRecoveries,
    IReadOnlyList<PlaybackBackupProviderStat> BackupProviders,
    string? ErrorNote);

public sealed record PlaybackSessionTotals(
    int RequestCount,
    long? FirstByteMs,
    long MaxOffset,
    int UpstreamStalls,
    long MaxUpstreamStallMs,
    long TotalUpstreamStallMs,
    long UpstreamWaitWallMs,
    long MaxUpstreamWaitWallMs,
    IReadOnlyList<PlaybackWaitWindow> UpstreamWaitWindows,
    /// <summary>
    /// Upstream waits that had already-downloaded segments queued behind the one
    /// the reader needed. Distinguishes "the source is too slow" from "one slow
    /// article is blocking a full buffer" — opposite problems, opposite fixes.
    /// </summary>
    int HeadOfLineStalls,
    long TotalHeadOfLineStallMs,
    int ActiveUpstreamWaits,
    int DownstreamStalls,
    long MaxDownstreamStallMs,
    long TotalDownstreamStallMs,
    int ActiveDownstreamWaits,
    int FallbackRescues,
    int ProviderRotations,
    int FallbackBudgetExhaustions,
    int CacheHits,
    int CacheMisses,
    int ConnectionPermitWaits,
    long MaxConnectionPermitWaitMs,
    int ProviderPoolWaits,
    long MaxProviderPoolWaitMs,
    int ZeroFilledSegments,
    long ZeroFilledBytes,
    int BodyStallRecoveries,
    IReadOnlyList<PlaybackBackupProviderStat> BackupProviders,
    string? ErrorNote);

internal sealed record PlaybackDiagnosticSnapshot(
    Guid SessionId,
    Guid RequestId,
    long BytesServed,
    long CurrentOffset,
    int BufferedSegments,
    int InFlightSegments,
    int UpstreamStalls,
    int DownstreamStalls,
    long MaxUpstreamStallMs,
    long MaxDownstreamStallMs,
    long TotalUpstreamStallMs,
    long TotalDownstreamStallMs,
    int HeadOfLineStalls,
    long TotalHeadOfLineStallMs,
    int FallbackRescues,
    int ProviderRotations,
    int FallbackBudgetExhaustions,
    int CacheHits,
    int CacheMisses,
    int ConnectionPermitWaits,
    long MaxConnectionPermitWaitMs,
    int ProviderPoolWaits,
    long MaxProviderPoolWaitMs,
    int ZeroFilledSegments,
    long ZeroFilledBytes,
    int BodyStallRecoveries,
    string BackupSummary);

internal sealed record PlaybackMetricsSnapshot(
    int UpstreamStalls,
    long MaxUpstreamStallMs,
    long TotalUpstreamStallMs,
    int HeadOfLineStalls,
    long TotalHeadOfLineStallMs,
    int ActiveUpstreamWaits,
    int DownstreamStalls,
    long MaxDownstreamStallMs,
    long TotalDownstreamStallMs,
    int ActiveDownstreamWaits,
    int FallbackRescues,
    int ProviderRotations,
    int FallbackBudgetExhaustions,
    int CacheHits,
    int CacheMisses,
    int ConnectionPermitWaits,
    long MaxConnectionPermitWaitMs,
    int ProviderPoolWaits,
    long MaxProviderPoolWaitMs,
    int ZeroFilledSegments,
    long ZeroFilledBytes,
    int BodyStallRecoveries,
    IReadOnlyList<PlaybackBackupProviderStat> BackupProviders)
{
    public string BackupSummary =>
        BackupProviders.Count == 0
            ? "none"
            : string.Join(
                ';',
                BackupProviders
                    .OrderBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
                    .Select(x =>
                        $"{x.Host}:attempts={x.Attempts}," +
                        $"rescued={x.Rescued}," +
                        $"missing={x.Missing}," +
                        $"timeouts={x.Timeouts}," +
                        $"errors={x.Errors}"));
}

internal readonly record struct PlaybackWaitUpdate(int Count, long MaxMs);

internal readonly record struct PlaybackSourcePressure(
    bool Stalled,
    bool ConnectionPermitStarved,
    bool ProviderPoolStarved)
{
    public bool Any => Stalled || ConnectionPermitStarved || ProviderPoolStarved;
}
