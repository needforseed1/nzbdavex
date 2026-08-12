namespace NzbWebDAV.Database.Models.Metrics;

public class ReadSession
{
    public Guid Id { get; set; }
    public long StartedAt { get; set; }
    public long EndedAt { get; set; }
    public int DurationMs { get; set; }
    public string Path { get; set; } = null!;
    public long? FileSize { get; set; }
    public long BytesServed { get; set; }
    public long BytesFetched { get; set; }
    public int FailoverSaves { get; set; }
    public string? ClientUserAgent { get; set; }
    public string? ClientIp { get; set; }
    public EndReasonCode EndReason { get; set; }

    // Content identity. Path is an opaque /content/{guid} for id-files, so the
    // playback page resolves display names through these instead.
    public string? FileName { get; set; }
    public Guid? DavItemId { get; set; }
    public Guid? HistoryItemId { get; set; }

    // Playback quality, folded up from the per-request diagnostics of every
    // range request that belonged to this session.
    public int RequestCount { get; set; }
    public int? FirstByteMs { get; set; }
    public long MaxOffset { get; set; }
    public int UpstreamStalls { get; set; }
    public int MaxUpstreamStallMs { get; set; }
    // Time actually spent waiting, which count and max together cannot express.
    public long TotalUpstreamStallMs { get; set; }
    // Wall-clock union of upstream waits. Unlike TotalUpstreamStallMs, concurrent
    // range requests cannot count the same second more than once.
    public long UpstreamWaitWallMs { get; set; }
    public int MaxUpstreamWaitWallMs { get; set; }

    /// <summary>
    /// The subset of <see cref="UpstreamStalls"/> where segments had already been
    /// downloaded and were queued behind the one the reader needed. A high share
    /// means the source kept up and one slow article held up the rest; a low
    /// share means the source genuinely could not deliver in time.
    /// </summary>
    public int HeadOfLineStalls { get; set; }

    public long TotalHeadOfLineStallMs { get; set; }
    public int DownstreamStalls { get; set; }
    public int MaxDownstreamStallMs { get; set; }
    public long TotalDownstreamStallMs { get; set; }
    public int FallbackRescues { get; set; }
    public int ProviderRotations { get; set; }
    public int FallbackBudgetExhaustions { get; set; }
    public int CacheHits { get; set; }
    public int CacheMisses { get; set; }
    public int ConnectionPermitWaits { get; set; }
    public int MaxConnectionPermitWaitMs { get; set; }
    public int ProviderPoolWaits { get; set; }
    public int MaxProviderPoolWaitMs { get; set; }

    /// <summary>
    /// Articles that could not be retrieved and were served to the player as
    /// zeros. Unlike every other counter here this one is not about delay: the
    /// bytes the viewer received were wrong.
    /// </summary>
    public int ZeroFilledSegments { get; set; }

    public long ZeroFilledBytes { get; set; }

    /// <summary>
    /// Bodies that stopped mid-transfer and were recovered by refetching. The
    /// stream survived, so nothing else on the row would record that a provider
    /// connection wedged.
    /// </summary>
    public int BodyStallRecoveries { get; set; }

    /// <summary>Time-weighted bytes queued ahead of the current article.</summary>
    public long? AverageReadAheadBytes { get; set; }

    /// <summary>
    /// Lowest queued byte count sustained for at least one second after the
    /// configured target was first reached; startup and the terminal EOF drain
    /// are deliberately excluded.
    /// </summary>
    public long? MinimumReadAheadBytes { get; set; }

    /// <summary>
    /// Per-provider breakdown, denormalised because SegmentFetches expire after
    /// 24 h while sessions are kept for 90 days. Hosts and nicknames stay out of
    /// it — those are resolved from live config when the page is rendered.
    /// </summary>
    public string? ProviderStatsJson { get; set; }

    public string? ErrorNote { get; set; }

    // Optional source/purpose enrichment for reads made through rclone. Exact
    // playback matches use DAV identity plus time; scanner/analyzer attribution
    // is explicitly stored as time-only when Plex exposes no media path.
    public string? PlexPurpose { get; set; }
    public string? PlexConfidence { get; set; }
    public string? PlexProduct { get; set; }
    public string? PlexPlayer { get; set; }
    public string? PlexPlatform { get; set; }
    public string? PlexRatingKey { get; set; }
    public string? PlexDetail { get; set; }
    public bool? PlexIsTranscode { get; set; }
    // Compact result only; raw Plex offsets and timelines are never persisted.
    public string? PlexPlaybackImpact { get; set; }

    public enum EndReasonCode
    {
        Completed = 0,
        Aborted = 1,
        Timeout = 2,
        Error = 3,
    }
}
