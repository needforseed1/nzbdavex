using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Api.Controllers.BenchmarkUsenetConnection;

[ApiController]
[Route("api/benchmark-usenet-connection")]
public class BenchmarkUsenetConnectionController(UsenetBenchmarkService benchmarkService) : BaseApiController
{
    private async Task<BenchmarkUsenetConnectionResponse> BenchmarkAsync(BenchmarkUsenetConnectionRequest request)
    {
        try
        {
            var result = await benchmarkService.RunAsync(
                request.ToConnectionDetails(),
                request.MaxConnections,
                request.Intensity,
                HttpContext.RequestAborted
            ).ConfigureAwait(false);
            return new BenchmarkUsenetConnectionResponse { Status = true, Result = result };
        }
        catch (CouldNotConnectToUsenetException)
        {
            return new BenchmarkUsenetConnectionResponse
            {
                Status = false,
                Error = "Couldn't connect to the provider. Check the host, port and SSL settings."
            };
        }
        catch (CouldNotLoginToUsenetException)
        {
            return new BenchmarkUsenetConnectionResponse
            {
                Status = false,
                Error = "Couldn't log in to the provider. Check the username and password."
            };
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new BenchmarkUsenetConnectionRequest(HttpContext);
        var response = await BenchmarkAsync(request).ConfigureAwait(false);
        return Ok(response);
    }
}
