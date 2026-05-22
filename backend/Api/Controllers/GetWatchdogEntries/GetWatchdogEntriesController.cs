using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetWatchdogEntries;

[ApiController]
[Route("api/get-watchdog-entries")]
public class GetWatchdogEntriesController(WatchdogLog watchdogLog) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var limitStr = HttpContext.Request.Query["limit"].ToString();
        var limit = int.TryParse(limitStr, out var n) ? Math.Clamp(n, 1, 500) : 200;

        var recent = await watchdogLog.GetRecentAsync(limit, HttpContext.RequestAborted).ConfigureAwait(false);
        var dtos = recent.Select(a => new GetWatchdogEntriesResponse.EntryDto
        {
            ClickId = a.ClickId.ToString(),
            AttemptedAtUnix = a.AttemptedAt.ToUnixTimeSeconds(),
            ContentType = a.ContentType,
            RequestedTitle = a.RequestedTitle,
            CandidateTitle = a.CandidateTitle,
            IndexerName = a.IndexerName,
            Size = a.Size,
            RankIndex = a.RankIndex,
            Outcome = a.Result,
            FailReason = a.FailReason,
            DurationMs = a.DurationMs,
            IsWinner = a.IsWinner,
            ProviderHost = a.ProviderHost,
        }).ToList();

        return Ok(new GetWatchdogEntriesResponse
        {
            Status = true,
            Entries = dtos,
        });
    }
}
