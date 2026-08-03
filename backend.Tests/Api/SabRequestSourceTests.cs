using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api;

public class SabRequestSourceTests
{
    [Fact]
    public void AcceptsNormalFrontendAndStreamingKeys()
    {
        Assert.True(SabRequestSource.IsValidApiKey("normal-key", "normal-key", "frontend-key"));
        Assert.True(SabRequestSource.IsValidApiKey("frontend-key", "normal-key", "frontend-key"));
        Assert.True(SabRequestSource.IsValidApiKey("streaming:normal-key", "normal-key", "frontend-key"));
        Assert.False(SabRequestSource.IsValidApiKey("streaming:wrong-key", "normal-key", "frontend-key"));
        Assert.False(SabRequestSource.IsValidApiKey(null, "normal-key", "frontend-key"));
    }

    [Theory]
    [InlineData("Sonarr/4.0.15.2941 (debian 12.0)", "sonarr")]
    [InlineData(" sonarr/4.0.15.2941", "sonarr")]
    [InlineData("Radarr/6.0.4.10291 (ubuntu 24.04)", "radarr")]
    [InlineData("RADARR (linux)", "radarr")]
    [InlineData("NotSonarr/1.0", null)]
    [InlineData("Mozilla/5.0", null)]
    [InlineData("", null)]
    public void NormalizesArrUserAgents(string userAgent, string? expected)
    {
        Assert.Equal(expected, SabRequestSource.GetArrSubmissionSource(userAgent));
    }

    [Fact]
    public void ExplicitStreamingKeyTakesPrecedenceOverUserAgent()
    {
        Assert.Equal(
            "streaming",
            SabRequestSource.GetSubmissionSource(
                "streaming:normal-key",
                "normal-key",
                "Sonarr/4.0"));
    }

    [Theory]
    [InlineData("Movies", "streaming", "streaming-movie")]
    [InlineData("movies", "streaming", "streaming-movie")]
    [InlineData("TV", "streaming", "streaming-series")]
    [InlineData("Movies", null, "Movies")]
    [InlineData("other", "streaming", "other")]
    public void CreatesStreamingDisplayCategories(
        string category,
        string? submissionSource,
        string expected)
    {
        Assert.Equal(
            expected,
            HistoryCategoryClassifier.GetDisplayCategory(category, submissionSource));
    }

    [Fact]
    public void VirtualFiltersSeparateStreamingFromPhysicalCategories()
    {
        var items = new[]
        {
            Item("arr-movie", "Movies", null),
            Item("streaming-movie", "Movies", "streaming"),
            Item("profile-movie", "streaming-movie", null),
            Item("streaming-series", "TV", "streaming"),
        }.AsQueryable();

        var movies = HistoryCategoryClassifier
            .ApplyFilter(items, "Movies", usePhysicalCategories: false)
            .Select(item => item.JobName)
            .ToArray();
        var streamingMovies = HistoryCategoryClassifier
            .ApplyFilter(items, "streaming-movie", usePhysicalCategories: false)
            .Select(item => item.JobName)
            .ToArray();
        var aioMovies = HistoryCategoryClassifier
            .ApplyFilter(items, "Movies", usePhysicalCategories: true)
            .Select(item => item.JobName)
            .ToArray();

        Assert.Equal(["arr-movie"], movies);
        Assert.Equal(["streaming-movie", "profile-movie"], streamingMovies);
        Assert.Equal(["arr-movie", "streaming-movie"], aioMovies);
    }

    private static HistoryItem Item(string jobName, string category, string? submissionSource)
    {
        return new HistoryItem
        {
            JobName = jobName,
            Category = category,
            SubmissionSource = submissionSource,
        };
    }
}
