using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Api.Controllers.TestPlexConnection;

public sealed class TestPlexConnectionRequest
{
    public string BaseUrl { get; }
    public string? Token { get; }

    public TestPlexConnectionRequest(HttpContext context)
    {
        BaseUrl = context.Request.Form["baseUrl"].FirstOrDefault()
                  ?? throw new BadHttpRequestException("Plex server URL is required.");
        Token = context.Request.Form["token"].FirstOrDefault();
    }
}
