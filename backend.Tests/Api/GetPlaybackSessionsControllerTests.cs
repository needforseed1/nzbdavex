using NzbWebDAV.Api.Controllers.GetPlaybackSessions;

namespace NzbWebDAV.Tests.Api;

public class GetPlaybackSessionsControllerTests
{
    [Theory]
    [InlineData("playback", "true", true)]
    [InlineData("plays", "TRUE", true)]
    [InlineData("mount", "true", false)]
    [InlineData("playback", "false", false)]
    [InlineData("playback", null, false)]
    public void DeepHistory_IsRestrictedToExplicitPlaybackRequests(
        string filter,
        string? deep,
        bool expected)
    {
        Assert.Equal(
            expected,
            GetPlaybackSessionsController.IsDeepPlaybackHistory(filter, deep));
    }

    [Fact]
    public void DeepPlayback_DoesNotLetTheRecentActivityLimitHideOlderRows()
    {
        var newestFirst = Enumerable.Range(1, 1_000).AsQueryable();

        var recent = GetPlaybackSessionsController
            .ApplyRawSessionLimit(newestFirst, deepPlayback: false, limit: 500)
            .ToList();
        var deepPlayback = GetPlaybackSessionsController
            .ApplyRawSessionLimit(newestFirst, deepPlayback: true, limit: 200)
            .ToList();

        Assert.Equal(500, recent.Count);
        Assert.Equal(1_000, deepPlayback.Count);
    }
}
