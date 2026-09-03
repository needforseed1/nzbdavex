using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RetryWatchdogNzb;

[ApiController]
[Route("api/retry-watchdog-nzb")]
public sealed class RetryWatchdogNzbController(WatchdogNzbRetryService retryService) : BaseApiController
{
    protected override IReadOnlySet<string>? AllowedMethods => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "POST" };

    protected override async Task<IActionResult> HandleRequest()
    {
        if (!long.TryParse(Request.Form["eventId"], out var eventId) || eventId <= 0)
            throw new BadHttpRequestException("A valid Watchdog eventId is required.");
        if (!Guid.TryParse(Request.Form["blobId"], out var blobId))
            throw new BadHttpRequestException("A valid selected blobId is required.");
        try
        {
            var result = await retryService.RetryAsync(eventId, blobId, HttpContext).ConfigureAwait(false);
            return Ok(new { status = true, queueItemId = result.QueueItemId, existing = result.Existing });
        }
        catch (KeyNotFoundException e)
        {
            return NotFound(new { status = false, error = e.Message });
        }
        catch (WatchdogRetryBusyException e)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { status = false, error = e.Message, busy = true });
        }
        catch (WatchdogRetryUnavailableException e)
        {
            return StatusCode(StatusCodes.Status410Gone, new { status = false, error = e.Message, unavailable = true });
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(new { status = false, error = e.Message });
        }
    }
}
