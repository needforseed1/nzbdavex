using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.ResolveWatchdogRetry;

[ApiController]
[Route("api/resolve-watchdog-retry")]
public sealed class ResolveWatchdogRetryController(WatchdogNzbRetryService retryService) : BaseApiController
{
    protected override IReadOnlySet<string>? AllowedMethods => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "POST" };

    protected override async Task<IActionResult> HandleRequest()
    {
        if (!long.TryParse(Request.Form["eventId"], out var eventId) || eventId <= 0)
            throw new BadHttpRequestException("A valid Watchdog eventId is required.");
        var resolution = await retryService.ResolveAsync(eventId, HttpContext.RequestAborted).ConfigureAwait(false);
        if (resolution is null) return NotFound(new { status = false, error = "Watchdog event was not found." });
        return Ok(new ResolveWatchdogRetryResponse
        {
            Status = true,
            Matches = resolution.Matches.Select(x => new ResolveWatchdogRetryResponse.MatchDto
            {
                BlobId = x.BlobId,
                Confidence = x.Confidence,
                SourceStatus = x.SourceStatus,
                Title = x.Title,
                Indexer = x.Indexer,
                Category = x.Category,
                Size = x.Size,
                CreatedAtUnix = x.CreatedAtUnix,
            }).ToList(),
        });
    }
}

public sealed class ResolveWatchdogRetryResponse : BaseApiResponse
{
    [JsonPropertyName("matches")] public required List<MatchDto> Matches { get; init; }

    public sealed class MatchDto
    {
        [JsonPropertyName("blobId")] public required Guid BlobId { get; init; }
        [JsonPropertyName("confidence")] public required string Confidence { get; init; }
        [JsonPropertyName("sourceStatus")] public required string SourceStatus { get; init; }
        [JsonPropertyName("title")] public required string Title { get; init; }
        [JsonPropertyName("indexer")] public string? Indexer { get; init; }
        [JsonPropertyName("category")] public required string Category { get; init; }
        [JsonPropertyName("size")] public required long Size { get; init; }
        [JsonPropertyName("createdAtUnix")] public required long CreatedAtUnix { get; init; }
    }
}
