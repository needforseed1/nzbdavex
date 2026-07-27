using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.ClearPlaybackSessions;

/// <summary>
/// Deletes playback history. Optional `olderThanDays` trims instead of wiping.
///
/// The overview page counts its "sessions" tiles from the same ReadSessions
/// table, so clearing here also removes those totals — call it out in the UI
/// before asking for confirmation.
/// </summary>
[ApiController]
[Route("api/clear-playback-sessions")]
public class ClearPlaybackSessionsController : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var olderThanDaysText = HttpContext.Request.Query["olderThanDays"].ToString();
        var cutoff = int.TryParse(olderThanDaysText, out var days) && days > 0
            ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds()
            : (long?)null;

        await using var metrics = new MetricsDbContext();
        var query = metrics.ReadSessions.AsQueryable();
        if (cutoff is { } endedBefore) query = query.Where(x => x.EndedAt < endedBefore);
        var deleted = await query.ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return Ok(new ClearPlaybackSessionsResponse
        {
            Status = true,
            Deleted = deleted,
        });
    }
}
