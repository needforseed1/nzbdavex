using System.Text.Json.Serialization;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetWatchdogEntries;

public class GetWatchdogEntriesResponse : BaseApiResponse
{
    [JsonPropertyName("entries")]
    public required List<EntryDto> Entries { get; init; }

    public class EntryDto
    {
        [JsonPropertyName("clickId")] public required string ClickId { get; init; }
        [JsonPropertyName("attemptedAtUnix")] public required long AttemptedAtUnix { get; init; }
        [JsonPropertyName("contentType")] public required string ContentType { get; init; }
        [JsonPropertyName("requestedTitle")] public required string RequestedTitle { get; init; }
        [JsonPropertyName("candidateTitle")] public required string CandidateTitle { get; init; }
        [JsonPropertyName("indexerName")] public required string IndexerName { get; init; }
        [JsonPropertyName("size")] public required long Size { get; init; }
        [JsonPropertyName("rankIndex")] public required int RankIndex { get; init; }
        [JsonPropertyName("outcome")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required WatchdogEntry.Outcome Outcome { get; init; }
        [JsonPropertyName("failReason")] public string? FailReason { get; init; }
        [JsonPropertyName("durationMs")] public required int DurationMs { get; init; }
        [JsonPropertyName("prepDurationMs")] public int? PrepDurationMs { get; init; }
        [JsonPropertyName("healthDurationMs")] public int? HealthDurationMs { get; init; }
        [JsonPropertyName("prepStats")] public PrepStatsDto? PrepStats { get; init; }
        [JsonPropertyName("healthStats")] public HealthStatsDto? HealthStats { get; init; }
        [JsonPropertyName("isWinner")] public required bool IsWinner { get; init; }
        [JsonPropertyName("providerHost")] public string? ProviderHost { get; init; }
        [JsonPropertyName("providerNickname")] public string? ProviderNickname { get; init; }
    }

    public class PrepStatsDto
    {
        [JsonPropertyName("fileCount")] public required int FileCount { get; init; }
        [JsonPropertyName("connections")] public required int Connections { get; init; }
        [JsonPropertyName("queueWaitMs")] public required long QueueWaitMs { get; init; }
        [JsonPropertyName("firstSegmentsMs")] public required long FirstSegmentsMs { get; init; }
        [JsonPropertyName("par2Ms")] public required long Par2Ms { get; init; }
        [JsonPropertyName("rarMs")] public required long RarMs { get; init; }
        [JsonPropertyName("processorsMs")] public required long ProcessorsMs { get; init; }
        [JsonPropertyName("lazyRarMounted")] public required bool LazyRarMounted { get; init; }
        [JsonPropertyName("firstSegmentFallbacks")] public required long FirstSegmentFallbacks { get; init; }
        [JsonPropertyName("lastStage")] public string? LastStage { get; init; }
        [JsonPropertyName("providers")] public required List<PrepProviderDto> Providers { get; init; }
    }

    public class PrepProviderDto
    {
        [JsonPropertyName("providerId")] public required string ProviderId { get; init; }
        [JsonPropertyName("host")] public required string Host { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("articles")] public required long Articles { get; init; }
        [JsonPropertyName("bytes")] public required long Bytes { get; init; }
        [JsonPropertyName("attempts")] public required long Attempts { get; init; }
        [JsonPropertyName("missing")] public required long Missing { get; init; }
        [JsonPropertyName("timeouts")] public required long Timeouts { get; init; }
        [JsonPropertyName("errors")] public required long Errors { get; init; }
        [JsonPropertyName("workMs")] public required long WorkMs { get; init; }
    }

    public class HealthStatsDto
    {
        [JsonPropertyName("totalArticles")] public required int TotalArticles { get; init; }
        [JsonPropertyName("foundArticles")] public int? FoundArticles { get; init; }
        [JsonPropertyName("missingArticles")] public int? MissingArticles { get; init; }
        [JsonPropertyName("providers")] public required List<HealthProviderDto> Providers { get; init; }
    }

    public class HealthProviderDto
    {
        [JsonPropertyName("providerId")] public required string ProviderId { get; init; }
        [JsonPropertyName("host")] public required string Host { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("preferred")] public required bool Preferred { get; init; }
        [JsonPropertyName("probeFound")] public required int ProbeFound { get; init; }
        [JsonPropertyName("probeReceived")] public required int ProbeReceived { get; init; }
        [JsonPropertyName("probeStatus")] public string? ProbeStatus { get; init; }
        [JsonPropertyName("batches")] public required long Batches { get; init; }
        [JsonPropertyName("attempted")] public required long Attempted { get; init; }
        [JsonPropertyName("received")] public required long Received { get; init; }
        [JsonPropertyName("found")] public required long Found { get; init; }
        [JsonPropertyName("missing")] public required long Missing { get; init; }
        [JsonPropertyName("failures")] public required long Failures { get; init; }
        [JsonPropertyName("workMs")] public required long WorkMs { get; init; }
        [JsonPropertyName("rate")] public required long Rate { get; init; }
    }
}
