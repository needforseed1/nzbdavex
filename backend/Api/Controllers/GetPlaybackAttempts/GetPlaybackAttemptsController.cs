using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetPlaybackAttempts;

[ApiController]
[Route("api/get-playback-attempts")]
public class GetPlaybackAttemptsController(PlaybackAttemptLog attemptLog) : BaseApiController
{
    protected override Task<IActionResult> HandleRequest()
    {
        var limitStr = HttpContext.Request.Query["limit"].ToString();
        var limit = int.TryParse(limitStr, out var n) ? Math.Clamp(n, 1, 500) : 200;

        var recent = attemptLog.GetRecent(limit);
        var dtos = recent.Select(a => new GetPlaybackAttemptsResponse.AttemptDto
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

        IActionResult result = Ok(new GetPlaybackAttemptsResponse
        {
            Status = true,
            Attempts = dtos,
        });
        return Task.FromResult(result);
    }
}
