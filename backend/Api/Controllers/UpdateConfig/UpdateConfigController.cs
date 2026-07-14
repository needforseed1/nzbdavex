using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(SettingsCoordinator coordinator) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        try
        {
            var result = await coordinator.UpdateFromApiAsync(
                request.ConfigItems, request.ResetKeys, request.BaseRevision, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            return Ok(new UpdateConfigResponse
            {
                Status = true,
                Revision = result.Revision,
                Warning = result.Warning,
            });
        }
        catch (StaleSettingsRevisionException e)
        {
            return Conflict(new UpdateConfigResponse
            {
                Status = false,
                Error = e.Message,
                Revision = e.Actual,
            });
        }
    }
}
