using System.Text.Json.Serialization;
using NzbWebDAV.Api.Controllers.GetPlaybackSessions;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessionDetail;

public class GetPlaybackSessionDetailResponse : BaseApiResponse
{
    [JsonPropertyName("session")]
    public required GetPlaybackSessionsResponse.SessionDto Session { get; init; }

    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("nzbName")] public string? NzbName { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }

    [JsonPropertyName("articleDetailAvailable")]
    public required bool ArticleDetailAvailable { get; init; }

    /// <summary>
    /// Whether the absence of article rows is explained by retention. Rows are
    /// kept for <see cref="ArticleRetentionHours"/> hours, but not every fetch
    /// path writes them, so "no rows" and "expired" are different answers and a
    /// session recorded minutes ago must not be told its detail has expired.
    /// </summary>
    [JsonPropertyName("articleDetailExpired")]
    public required bool ArticleDetailExpired { get; init; }

    [JsonPropertyName("articleRetentionHours")]
    public required int ArticleRetentionHours { get; init; }

    [JsonPropertyName("articles")]
    public required List<ArticleFetchDto> Articles { get; init; }

    [JsonPropertyName("articleCounts")]
    public required List<ArticleCountDto> ArticleCounts { get; init; }

    public class ArticleFetchDto
    {
        [JsonPropertyName("atUnix")] public required long AtUnix { get; init; }
        [JsonPropertyName("atMs")] public required long AtMs { get; init; }
        [JsonPropertyName("providerId")] public required string ProviderId { get; init; }
        [JsonPropertyName("host")] public required string Host { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required SegmentFetch.FetchStatus Status { get; init; }
        [JsonPropertyName("durationMs")] public required int DurationMs { get; init; }
        [JsonPropertyName("retries")] public required int Retries { get; init; }
        [JsonPropertyName("bytes")] public required long Bytes { get; init; }
    }

    public class ArticleCountDto
    {
        [JsonPropertyName("providerId")] public required string ProviderId { get; init; }
        [JsonPropertyName("host")] public required string Host { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("status")]
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required SegmentFetch.FetchStatus Status { get; init; }
        [JsonPropertyName("count")] public required int Count { get; init; }
        [JsonPropertyName("avgDurationMs")] public required int AvgDurationMs { get; init; }
        [JsonPropertyName("maxDurationMs")] public required int MaxDurationMs { get; init; }
    }
}
