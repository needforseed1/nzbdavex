using NzbWebDAV.Api.Controllers.Profiles;

namespace NzbWebDAV.Tests.Api;

public class ProfilePlaybackCategoryTests
{
    [Theory]
    [InlineData("movie", "streaming-movie")]
    [InlineData("MOVIE", "streaming-movie")]
    [InlineData("series", "streaming-series")]
    [InlineData(" series ", "streaming-series")]
    public void UsesDedicatedCategoriesForProfilePlayback(string contentType, string expected)
    {
        Assert.Equal(expected, ProfilePlayController.GetPlaybackCategory(contentType, "uncategorized"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void FallsBackForUnknownPlaybackTypes(string? contentType)
    {
        Assert.Equal(
            "uncategorized",
            ProfilePlayController.GetPlaybackCategory(contentType, "uncategorized"));
    }
}
