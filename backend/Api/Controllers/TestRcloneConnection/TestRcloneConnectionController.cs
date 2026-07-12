using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Rclone;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestRcloneConnection;

[ApiController]
[Route("api/test-rclone-connection")]
public class TestRcloneConnectionController(ConfigManager configManager) : BaseApiController
{
    private async Task<TestRcloneConnectionResponse> TestRcloneConnection(TestRcloneConnectionRequest request)
    {
        try
        {
            var sameStoredEndpoint = string.Equals(
                    request.Host.Trim().TrimEnd('/'),
                    configManager.GetRcloneHost()?.Trim().TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.User?.Trim(), configManager.GetRcloneUser()?.Trim(), StringComparison.Ordinal);
            var password = string.IsNullOrWhiteSpace(request.Pass) && sameStoredEndpoint
                ? configManager.GetRclonePass()
                : request.Pass;
            var result = await RcloneClient
                .TestConnection(request.Host, request.User, password)
                .ConfigureAwait(false);

            return new TestRcloneConnectionResponse
            {
                Status = true,
                Connected = result.Success,
                Error = result.Error
            };
        }
        catch (Exception e)
        {
            return new TestRcloneConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message
            };
        }
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestRcloneConnectionRequest(HttpContext);
        var response = await TestRcloneConnection(request).ConfigureAwait(false);
        return Ok(response);
    }
}
