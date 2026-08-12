using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessions;

public class GetPlaybackSessionsResponse : BaseApiResponse
{
    [JsonPropertyName("plays")]
    public required List<PlayDto> Plays { get; init; }

    [JsonPropertyName("plexStatus")]
    public required PlexStatusDto PlexStatus { get; init; }

    /// <summary>
    /// Raw session rows this answer was built from. Plays are grouped after the
    /// rows are sampled, so a page that does not say how deep the sample went
    /// presents "3 plays with issues" as a total when it is a count over the
    /// most recent <see cref="SampledSessions"/> reads.
    /// </summary>
    [JsonPropertyName("sampledSessions")]
    public required int SampledSessions { get; init; }

    /// <summary>
    /// For the recent activity view, true when the raw sample hit its limit, so
    /// older reads are not represented. For deep playback history, true when
    /// the retained history contains more matching grouped plays than requested.
    /// </summary>
    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    /// <summary>
    /// One continuous access to one file by one client. Playback clients end and
    /// reopen read sessions on seek, pause, or codec changes, while mount clients
    /// do the same as their caller reads. Related raw sessions are grouped before
    /// their purpose is classified.
    /// </summary>
    public class PlayDto
    {
        [JsonPropertyName("key")] public required string Key { get; init; }
        [JsonPropertyName("title")] public required string Title { get; init; }
        [JsonPropertyName("nzbName")] public string? NzbName { get; init; }
        [JsonPropertyName("category")] public string? Category { get; init; }
        /// <summary>
        /// Normalized application that originally submitted the NZB. This is
        /// provenance, not necessarily the application performing a later read.
        /// </summary>
        [JsonPropertyName("submissionSource")] public string? SubmissionSource { get; init; }
        [JsonPropertyName("path")] public required string Path { get; init; }
        [JsonPropertyName("davItemId")] public string? DavItemId { get; init; }
        [JsonPropertyName("historyItemId")] public string? HistoryItemId { get; init; }
        [JsonPropertyName("clientIp")] public string? ClientIp { get; init; }
        [JsonPropertyName("clientUserAgent")] public string? ClientUserAgent { get; init; }
        [JsonPropertyName("startedAtUnix")] public required long StartedAtUnix { get; init; }
        [JsonPropertyName("endedAtUnix")] public required long EndedAtUnix { get; init; }
        [JsonPropertyName("watchedMs")] public required long WatchedMs { get; init; }
        [JsonPropertyName("spanMs")] public required long SpanMs { get; init; }
        [JsonPropertyName("fileSize")] public long? FileSize { get; init; }
        [JsonPropertyName("maxOffset")] public required long MaxOffset { get; init; }
        [JsonPropertyName("reachedPct")] public double? ReachedPct { get; init; }
        [JsonPropertyName("bytesServed")] public required long BytesServed { get; init; }
        [JsonPropertyName("bytesFetched")] public required long BytesFetched { get; init; }
        [JsonPropertyName("avgBytesPerSecond")] public required long AvgBytesPerSecond { get; init; }
        /// <summary>
        /// What the source delivered, against what the client consumed
        /// (<see cref="AvgBytesPerSecond"/>). Two similar rates mean the client set
        /// the pace; a source rate far below the client rate means usenet did.
        /// </summary>
        [JsonPropertyName("sourceBytesPerSecond")] public required long SourceBytesPerSecond { get; init; }
        [JsonPropertyName("firstByteMs")] public int? FirstByteMs { get; init; }
        [JsonPropertyName("averageReadAheadBytes")] public long? AverageReadAheadBytes { get; init; }
        [JsonPropertyName("minimumReadAheadBytes")] public long? MinimumReadAheadBytes { get; init; }
        [JsonPropertyName("endReason")] public required string EndReason { get; init; }
        [JsonPropertyName("errorNote")] public string? ErrorNote { get; init; }
        [JsonPropertyName("hasDiagnostics")] public required bool HasDiagnostics { get; init; }
        /// <summary>
        /// A tiny successful read, commonly a header or metadata probe. The
        /// caller's exact intent is not observable from WebDAV.
        /// </summary>
        [JsonPropertyName("isProbe")] public required bool IsProbe { get; init; }
        /// <summary>
        /// The WebDAV request came from rclone. The process or container reading
        /// the shared mount is not visible in the HTTP user agent.
        /// </summary>
        [JsonPropertyName("isRcloneActivity")] public required bool IsRcloneActivity { get; init; }
        /// <summary>
        /// A direct, substantial read whose transfer pattern looks like
        /// playback. The user agent is deliberately not used as a requirement:
        /// proxies and Android player stacks often reduce it to a generic name.
        /// rclone remains excluded because its shared-mount caller is opaque.
        /// </summary>
        [JsonPropertyName("isReliablePlayback")] public required bool IsReliablePlayback { get; init; }
        /// <summary>
        /// A conservative access-pattern match for background mount activity:
        /// either repeated short tail probes, or concurrent large bulk reads.
        /// The row remains in history and in the mount-activity view.
        /// </summary>
        [JsonPropertyName("isLikelyBackgroundActivity")]
        public required bool IsLikelyBackgroundActivity { get; set; }
        /// <summary>
        /// A specific mount-side purpose NzbDAVex can infer without knowing the
        /// process behind rclone. "symlink-resolution" is exact from the
        /// requested .rclonelink file; "import-inspection" requires either a
        /// multi-file batch or a matching .rclonelink followed by a brief
        /// head/tail inspection of one newly completed file; "analysis-probe"
        /// is a zero-transfer burst that reaches the end of a media file.
        /// </summary>
        [JsonPropertyName("mountPurpose")] public string? MountPurpose { get; set; }
        [JsonPropertyName("mountRelatedFileCount")] public int? MountRelatedFileCount { get; set; }
        [JsonPropertyName("mountCompletedAtUnix")] public long? MountCompletedAtUnix { get; set; }
        [JsonPropertyName("plexPurpose")] public string? PlexPurpose { get; init; }
        [JsonPropertyName("plexConfidence")] public string? PlexConfidence { get; init; }
        [JsonPropertyName("plexProduct")] public string? PlexProduct { get; init; }
        [JsonPropertyName("plexPlayer")] public string? PlexPlayer { get; init; }
        [JsonPropertyName("plexPlatform")] public string? PlexPlatform { get; init; }
        [JsonPropertyName("plexRatingKey")] public string? PlexRatingKey { get; init; }
        [JsonPropertyName("plexDetail")] public string? PlexDetail { get; init; }
        [JsonPropertyName("plexIsTranscode")] public bool? PlexIsTranscode { get; init; }
        /// <summary>
        /// Compact correlation result. Raw Plex offsets and timelines are not stored.
        /// </summary>
        [JsonPropertyName("plexPlaybackImpact")] public string? PlexPlaybackImpact { get; init; }
        /// <summary>
        /// True for an exact media match or a unique time-only Plex session
        /// observed playing. It identifies likely source/purpose, not watch time.
        /// </summary>
        [JsonPropertyName("isPlexPlayback")] public bool IsPlexPlayback { get; set; }
        [JsonPropertyName("issues")] public required List<string> Issues { get; init; }
        [JsonPropertyName("counters")] public required CountersDto Counters { get; init; }
        [JsonPropertyName("providers")] public required List<ProviderDto> Providers { get; init; }
        [JsonPropertyName("sessions")] public required List<SessionDto> Sessions { get; init; }

        // Used only while classifying the response. The completion timestamp is
        // exposed above only after a strong import-batch match.
        [JsonIgnore] public long? ContentCompletedAtUnix { get; init; }
    }

    public class PlexStatusDto
    {
        [JsonPropertyName("enabled")] public required bool Enabled { get; init; }
        [JsonPropertyName("connected")] public required bool Connected { get; init; }
        [JsonPropertyName("lastSuccessfulPollAtUnix")]
        public long? LastSuccessfulPollAtUnix { get; init; }
        [JsonPropertyName("lastError")] public string? LastError { get; init; }
        [JsonPropertyName("serverName")] public string? ServerName { get; init; }
        [JsonPropertyName("serverVersion")] public string? ServerVersion { get; init; }
        [JsonPropertyName("activitiesConnected")] public bool? ActivitiesConnected { get; init; }
        [JsonPropertyName("activitiesError")] public string? ActivitiesError { get; init; }
    }

    public class SessionDto
    {
        [JsonPropertyName("id")] public required string Id { get; init; }
        [JsonPropertyName("path")] public required string Path { get; init; }
        [JsonPropertyName("fileName")] public string? FileName { get; init; }
        [JsonPropertyName("davItemId")] public string? DavItemId { get; init; }
        [JsonPropertyName("historyItemId")] public string? HistoryItemId { get; init; }
        [JsonPropertyName("clientIp")] public string? ClientIp { get; init; }
        [JsonPropertyName("clientUserAgent")] public string? ClientUserAgent { get; init; }
        [JsonPropertyName("startedAtUnix")] public required long StartedAtUnix { get; init; }
        [JsonPropertyName("endedAtUnix")] public required long EndedAtUnix { get; init; }
        [JsonPropertyName("startedAtMs")] public required long StartedAtMs { get; init; }
        [JsonPropertyName("endedAtMs")] public required long EndedAtMs { get; init; }
        [JsonPropertyName("durationMs")] public required int DurationMs { get; init; }
        [JsonPropertyName("requestCount")] public required int RequestCount { get; init; }
        [JsonPropertyName("bytesServed")] public required long BytesServed { get; init; }
        [JsonPropertyName("bytesFetched")] public required long BytesFetched { get; init; }
        [JsonPropertyName("fileSize")] public long? FileSize { get; init; }
        [JsonPropertyName("maxOffset")] public required long MaxOffset { get; init; }
        [JsonPropertyName("firstByteMs")] public int? FirstByteMs { get; init; }
        [JsonPropertyName("averageReadAheadBytes")] public long? AverageReadAheadBytes { get; init; }
        [JsonPropertyName("minimumReadAheadBytes")] public long? MinimumReadAheadBytes { get; init; }
        [JsonPropertyName("endReason")] public required string EndReason { get; init; }
        [JsonPropertyName("errorNote")] public string? ErrorNote { get; init; }
        /// <summary>
        /// False for sessions recorded before playback diagnostics existed, so the
        /// UI can say "not recorded" instead of implying a flawless stream.
        /// </summary>
        [JsonPropertyName("hasDiagnostics")] public required bool HasDiagnostics { get; init; }
        [JsonPropertyName("plexPurpose")] public string? PlexPurpose { get; init; }
        [JsonPropertyName("plexConfidence")] public string? PlexConfidence { get; init; }
        [JsonPropertyName("plexProduct")] public string? PlexProduct { get; init; }
        [JsonPropertyName("plexPlayer")] public string? PlexPlayer { get; init; }
        [JsonPropertyName("plexPlatform")] public string? PlexPlatform { get; init; }
        [JsonPropertyName("plexRatingKey")] public string? PlexRatingKey { get; init; }
        [JsonPropertyName("plexDetail")] public string? PlexDetail { get; init; }
        [JsonPropertyName("plexIsTranscode")] public bool? PlexIsTranscode { get; init; }
        [JsonPropertyName("plexPlaybackImpact")] public string? PlexPlaybackImpact { get; init; }
        [JsonPropertyName("issues")] public required List<string> Issues { get; init; }
        [JsonPropertyName("counters")] public required CountersDto Counters { get; init; }
        [JsonPropertyName("providers")] public required List<ProviderDto> Providers { get; init; }
    }

    public class CountersDto
    {
        [JsonPropertyName("upstreamStalls")] public required int UpstreamStalls { get; init; }
        [JsonPropertyName("maxUpstreamStallMs")] public required int MaxUpstreamStallMs { get; init; }
        [JsonPropertyName("totalUpstreamStallMs")] public required long TotalUpstreamStallMs { get; init; }
        [JsonPropertyName("upstreamWaitWallMs")] public required long UpstreamWaitWallMs { get; init; }
        [JsonPropertyName("maxUpstreamWaitWallMs")] public required int MaxUpstreamWaitWallMs { get; init; }
        /// <summary>
        /// Waits where downloaded segments sat behind the one the reader needed:
        /// the source kept up, one slow article did not. The remainder of
        /// <see cref="UpstreamStalls"/> is the source genuinely falling behind.
        /// </summary>
        [JsonPropertyName("headOfLineStalls")] public required int HeadOfLineStalls { get; init; }
        [JsonPropertyName("totalHeadOfLineStallMs")] public required long TotalHeadOfLineStallMs { get; init; }
        [JsonPropertyName("downstreamStalls")] public required int DownstreamStalls { get; init; }
        [JsonPropertyName("maxDownstreamStallMs")] public required int MaxDownstreamStallMs { get; init; }
        [JsonPropertyName("totalDownstreamStallMs")] public required long TotalDownstreamStallMs { get; init; }
        [JsonPropertyName("fallbackRescues")] public required int FallbackRescues { get; init; }
        [JsonPropertyName("providerRotations")] public required int ProviderRotations { get; init; }
        [JsonPropertyName("fallbackBudgetExhaustions")] public required int FallbackBudgetExhaustions { get; init; }
        [JsonPropertyName("cacheHits")] public required int CacheHits { get; init; }
        [JsonPropertyName("cacheMisses")] public required int CacheMisses { get; init; }
        [JsonPropertyName("connectionPermitWaits")] public required int ConnectionPermitWaits { get; init; }
        [JsonPropertyName("maxConnectionPermitWaitMs")] public required int MaxConnectionPermitWaitMs { get; init; }
        [JsonPropertyName("providerPoolWaits")] public required int ProviderPoolWaits { get; init; }
        [JsonPropertyName("maxProviderPoolWaitMs")] public required int MaxProviderPoolWaitMs { get; init; }
        [JsonPropertyName("failoverSaves")] public required int FailoverSaves { get; init; }
        /// <summary>
        /// Articles served to the player as zeros because they could not be
        /// retrieved. The only counter here that means wrong data, not late data.
        /// </summary>
        [JsonPropertyName("zeroFilledSegments")] public required int ZeroFilledSegments { get; init; }
        [JsonPropertyName("zeroFilledBytes")] public required long ZeroFilledBytes { get; init; }
        /// <summary>Bodies that went silent mid-transfer and had to be refetched.</summary>
        [JsonPropertyName("bodyStallRecoveries")] public required int BodyStallRecoveries { get; init; }
    }

    public class ProviderDto
    {
        [JsonPropertyName("providerId")] public required string ProviderId { get; init; }
        [JsonPropertyName("host")] public required string Host { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("segments")] public required long Segments { get; init; }
        [JsonPropertyName("bytes")] public required long Bytes { get; init; }
        [JsonPropertyName("attempts")] public required long Attempts { get; init; }
        [JsonPropertyName("rescued")] public required long Rescued { get; init; }
        [JsonPropertyName("missing")] public required long Missing { get; init; }
        [JsonPropertyName("timeouts")] public required long Timeouts { get; init; }
        [JsonPropertyName("errors")] public required long Errors { get; init; }
        [JsonPropertyName("isBackup")] public required bool IsBackup { get; init; }
    }
}
