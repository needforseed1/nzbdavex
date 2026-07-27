using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessions;

public class GetPlaybackSessionsResponse : BaseApiResponse
{
    [JsonPropertyName("plays")]
    public required List<PlayDto> Plays { get; init; }

    /// <summary>
    /// Raw session rows this answer was built from. Plays are grouped after the
    /// rows are sampled, so a page that does not say how deep the sample went
    /// presents "3 plays with issues" as a total when it is a count over the
    /// most recent <see cref="SampledSessions"/> reads.
    /// </summary>
    [JsonPropertyName("sampledSessions")]
    public required int SampledSessions { get; init; }

    /// <summary>
    /// True when the sample hit its limit, so older reads exist that are not
    /// represented and the oldest play shown may be missing its earlier parts.
    /// </summary>
    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    /// <summary>
    /// One continuous viewing of one file by one client. Players end and reopen
    /// read sessions constantly — on seek, on pause, on codec switch — so the raw
    /// session rows are grouped back into the thing a person would call a play.
    /// </summary>
    public class PlayDto
    {
        [JsonPropertyName("key")] public required string Key { get; init; }
        [JsonPropertyName("title")] public required string Title { get; init; }
        [JsonPropertyName("nzbName")] public string? NzbName { get; init; }
        [JsonPropertyName("category")] public string? Category { get; init; }
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
        [JsonPropertyName("endReason")] public required string EndReason { get; init; }
        [JsonPropertyName("errorNote")] public string? ErrorNote { get; init; }
        [JsonPropertyName("hasDiagnostics")] public required bool HasDiagnostics { get; init; }
        /// <summary>
        /// A media-scanner header read rather than someone watching something.
        /// </summary>
        [JsonPropertyName("isProbe")] public required bool IsProbe { get; init; }
        [JsonPropertyName("issues")] public required List<string> Issues { get; init; }
        [JsonPropertyName("counters")] public required CountersDto Counters { get; init; }
        [JsonPropertyName("providers")] public required List<ProviderDto> Providers { get; init; }
        [JsonPropertyName("sessions")] public required List<SessionDto> Sessions { get; init; }
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
        [JsonPropertyName("endReason")] public required string EndReason { get; init; }
        [JsonPropertyName("errorNote")] public string? ErrorNote { get; init; }
        /// <summary>
        /// False for sessions recorded before playback diagnostics existed, so the
        /// UI can say "not recorded" instead of implying a flawless stream.
        /// </summary>
        [JsonPropertyName("hasDiagnostics")] public required bool HasDiagnostics { get; init; }
        [JsonPropertyName("issues")] public required List<string> Issues { get; init; }
        [JsonPropertyName("counters")] public required CountersDto Counters { get; init; }
        [JsonPropertyName("providers")] public required List<ProviderDto> Providers { get; init; }
    }

    public class CountersDto
    {
        [JsonPropertyName("upstreamStalls")] public required int UpstreamStalls { get; init; }
        [JsonPropertyName("maxUpstreamStallMs")] public required int MaxUpstreamStallMs { get; init; }
        [JsonPropertyName("totalUpstreamStallMs")] public required long TotalUpstreamStallMs { get; init; }
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
