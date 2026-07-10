using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Tests.Services.Benchmark;

public class HealthPipelineRecommendationTests
{
    [Fact]
    public void SelectsSmallestReliableDepthWithinFivePercentOfBestRate()
    {
        var points = new[]
        {
            Point(8, 950, reliable: true),
            Point(16, 1_000, reliable: true),
            Point(32, 1_020, reliable: true),
        };

        Assert.Equal(16, HealthPipelineRecommendation.Select(points));
    }

    [Fact]
    public void RejectsFasterDepthWhenItWasNotReliable()
    {
        var points = new[]
        {
            Point(8, 500, reliable: true),
            Point(16, 900, reliable: false),
            Point(32, 800, reliable: true),
        };

        Assert.Equal(32, HealthPipelineRecommendation.Select(points));
    }

    [Fact]
    public void ReturnsNullWhenEveryDepthFailed()
    {
        var points = new[]
        {
            Point(1, 0, reliable: false),
            Point(8, 1_000, reliable: false),
        };

        Assert.Null(HealthPipelineRecommendation.Select(points));
    }

    private static BenchmarkHealthPipeliningPoint Point(int depth, double rate, bool reliable) => new()
    {
        Depth = depth,
        StatsPerSecond = rate,
        Reliable = reliable,
    };
}
