using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.SettingsSync;

[ApiController]
[Route("api/settings-sync")]
public class SettingsSyncController(SettingsCoordinator coordinator) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpContext.Request.Method == HttpMethods.Post)
            await coordinator.RebuildFileAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { status = true, sync = coordinator.Status });
    }
}
