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
