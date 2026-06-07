using System.IO.Compression;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Warden;

[ApiController]
[Route("api/warden-import")]
public class WardenImportController(WardenStore warden) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var action = HttpContext.Request.HasFormContentType
            ? HttpContext.Request.Form["action"].ToString()
            : "";

        if (action == "clear")
        {
            var removed = warden.Clear();
            return Ok(new WardenImportResponse { Status = true, Added = 0, Total = warden.Count, Cleared = removed });
        }

        if (!HttpContext.Request.HasFormContentType || HttpContext.Request.Form.Files.Count == 0)
            throw new BadHttpRequestException("No file was uploaded.");

        var file = HttpContext.Request.Form.Files[0];
        var before = warden.Count;

        await using (var raw = file.OpenReadStream())
        {
            if (file.FileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var gz = new GZipStream(raw, CompressionMode.Decompress);
                await warden.ImportFromAsync(gz, ct);
            }
            else
            {
                await warden.ImportFromAsync(raw, ct);
            }
        }

        var after = warden.Count;
        return Ok(new WardenImportResponse
        {
            Status = true,
            Added = Math.Max(0, after - before),
            Total = after,
            Cleared = 0,
        });
    }
}

public class WardenImportResponse : BaseApiResponse
{
    [JsonPropertyName("added")] public required int Added { get; init; }
    [JsonPropertyName("total")] public required int Total { get; init; }
    [JsonPropertyName("cleared")] public required int Cleared { get; init; }
}
