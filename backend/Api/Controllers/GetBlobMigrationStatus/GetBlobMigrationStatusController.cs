using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetBlobMigrationStatus;

[ApiController]
[Route("api/get-blob-migration-status")]
public class GetBlobMigrationStatusController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var remaining = await dbClient.Ctx.NzbFiles.CountAsync(ct).ConfigureAwait(false)
            + await dbClient.Ctx.RarFiles.CountAsync(ct).ConfigureAwait(false)
            + await dbClient.Ctx.MultipartFiles.CountAsync(ct).ConfigureAwait(false);
        return Ok(new GetBlobMigrationStatusResponse
        {
            Status = true,
            Remaining = remaining,
        });
    }
}

public class GetBlobMigrationStatusResponse : BaseApiResponse
{
    public int Remaining { get; init; }
}
