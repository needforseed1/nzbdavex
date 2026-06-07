using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-export")]
public class WardenExportController(WardenStore warden) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        Response.ContentType = "application/gzip";
        Response.Headers["Content-Disposition"] = "attachment; filename=\"warden.ndjson.gz\"";
        await using var gz = new GZipStream(Response.Body, CompressionLevel.Optimal, leaveOpen: true);
        await warden.ExportToAsync(gz, ct);
        return new EmptyResult();
    }
}
