using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Clients.Plex;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Controllers.TestPlexConnection;

[ApiController]
[Route("api/test-plex-connection")]
public sealed class TestPlexConnectionController(
    ConfigManager configManager,
    PlexClient plexClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new TestPlexConnectionRequest(HttpContext);
        var normalizedUrl = request.BaseUrl.Trim().TrimEnd('/');
        var sameStoredEndpoint = string.Equals(
            normalizedUrl,
            configManager.GetPlexBaseUrl(),
            StringComparison.OrdinalIgnoreCase);
        var token = string.IsNullOrWhiteSpace(request.Token) && sameStoredEndpoint
            ? configManager.GetPlexToken()
            : request.Token;
        if (string.IsNullOrWhiteSpace(token))
            throw new BadHttpRequestException("Plex token is required.");

        try
        {
            var server = await plexClient.GetServerInfoAsync(
                    normalizedUrl, token, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            var sessions = await plexClient.GetSessionsAsync(
                    normalizedUrl, token, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            bool activitiesAvailable;
            string? activitiesError = null;
            try
            {
                await plexClient.GetActivitiesAsync(
                        normalizedUrl, token, HttpContext.RequestAborted)
                    .ConfigureAwait(false);
                activitiesAvailable = true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The activity endpoint is optional. Playback session
                // classification remains useful when this compatibility probe
                // is absent or temporarily unavailable.
                activitiesAvailable = false;
                activitiesError = e.Message;
            }
            return Ok(new TestPlexConnectionResponse
            {
                Status = true,
                Connected = true,
                ServerName = server.Name,
                ServerVersion = server.Version,
                ActiveSessions = sessions.Count,
                ActivitiesAvailable = activitiesAvailable,
                ActivitiesError = activitiesError,
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Ok(new TestPlexConnectionResponse
            {
                Status = true,
                Connected = false,
                Error = e.Message,
            });
        }
    }
}
