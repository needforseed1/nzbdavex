using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetConfig;

[ApiController]
[Route("api/get-config")]
public class GetConfigController(DavDatabaseClient dbClient) : BaseApiController
{
    private async Task<GetConfigResponse> GetConfig(GetConfigRequest request)
    {
        var storedItems = await dbClient.Ctx.ConfigItems
            .Where(x => request.ConfigKeys.Contains(x.ConfigName))
            .AsNoTracking()
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        var response = new GetConfigResponse { ConfigItems = MaskSensitiveValues(storedItems) };
        return response;
    }

    internal static List<ConfigItem> MaskSensitiveValues(IEnumerable<ConfigItem> configItems)
    {
        return configItems.Select(item => new ConfigItem
        {
            ConfigName = item.ConfigName,
            ConfigValue = item.ConfigName is "webdav.pass" or "rclone.pass" ? "" : item.ConfigValue,
        }).ToList();
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetConfigRequest(HttpContext);
        var response = await GetConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}
