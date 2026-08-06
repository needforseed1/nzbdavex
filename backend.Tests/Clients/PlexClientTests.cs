using System.Net;
using System.Text;
using NzbWebDAV.Clients.Plex;

namespace NzbWebDAV.Tests.Clients;

public class PlexClientTests
{
    [Fact]
    public void ActivityNotificationsExposeShortLivedAnalyzerJobs()
    {
        var activities = PlexClient.ParseActivityNotifications(
            """
            {"NotificationContainer":{"type":"activity","size":1,
             "ActivityNotification":[{
               "event":"started","uuid":"outer-id",
               "Activity":{"uuid":"analysis-id","type":"media.analyze",
                 "title":"Analyzing media","subtitle":"Movies"}
             }]}}
            """u8.ToArray());

        var activity = Assert.Single(activities);
        Assert.Equal("analysis-id", activity.Key);
        Assert.Equal("media.analyze", activity.Type);
        Assert.Equal("Analyzing media", activity.Title);
        Assert.Equal("Movies", activity.Subtitle);
    }

    [Fact]
    public async Task ActiveSessionsExposeOnlyTheFieldsNeededForClassification()
    {
        var handler = new StubHandler();
        using var client = new PlexClient(handler);

        var server = await client.GetServerInfoAsync(
            "http://plex:32400", "secret-token", CancellationToken.None);
        var session = Assert.Single(await client.GetSessionsAsync(
            "http://plex:32400", "secret-token", CancellationToken.None));
        var activity = Assert.Single(await client.GetActivitiesAsync(
            "http://plex:32400", "secret-token", CancellationToken.None));

        Assert.Equal("Living Room", server.Name);
        Assert.Equal("server-id", server.MachineIdentifier);
        Assert.Equal("session-id", session.SessionId);
        Assert.Equal("42", session.RatingKey);
        Assert.Equal("/media/Movies/Example.mkv", session.MediaPartPath);
        Assert.Equal("paused", session.State);
        Assert.Equal(844_000, session.ViewOffsetMs);
        Assert.Equal("Plex Web", session.Product);
        Assert.True(session.IsTranscode);
        Assert.Equal("activity-id", activity.Key);
        Assert.Equal("library.update.section", activity.Type);
        Assert.Equal("Scanning Movies", activity.Title);
        Assert.All(handler.RequestTokens, token => Assert.Equal("secret-token", token));
        Assert.All(handler.RequestUris, uri => Assert.DoesNotContain("secret-token", uri));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public List<string?> RequestTokens { get; } = [];
        public List<string> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestTokens.Add(request.Headers.GetValues("X-Plex-Token").Single());
            RequestUris.Add(request.RequestUri!.ToString());
            var json = request.RequestUri.AbsolutePath switch
            {
                "/" => """
                  {"MediaContainer":{"friendlyName":"Living Room","version":"1.2.3","machineIdentifier":"server-id"}}
                  """,
                "/activities" => """
                  {"MediaContainer":{"Activity":[{
                    "uuid":"activity-id","type":"library.update.section",
                    "title":"Scanning Movies","subtitle":"Looking for changes","progress":37.5
                  }]}}
                  """,
                _ => """
                  {"MediaContainer":{"Metadata":[{
                    "sessionKey":"session-key","ratingKey":"42","key":"/library/metadata/42",
                    "title":"Example","type":"movie","duration":7200000,"viewOffset":844000,
                    "Session":{"id":"session-id"},
                    "Player":{"state":"paused","machineIdentifier":"player-id","title":"Chrome",
                              "product":"Plex Web","version":"4.160.0","platform":"Chrome"},
                    "Media":[{"Part":[{"key":"/library/parts/1","file":"/media/Movies/Example.mkv","size":1234}]}],
                    "TranscodeSession":{"key":"/transcode/session","protocol":"dash",
                                        "videoDecision":"transcode","audioDecision":"copy"}
                  }]}}
                  """,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
