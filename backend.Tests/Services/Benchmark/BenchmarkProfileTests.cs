using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Tests.Services.Benchmark;

public class BenchmarkProfileTests
{
    [Theory]
    [InlineData(BenchmarkIntensity.Quick)]
    [InlineData(BenchmarkIntensity.Thorough)]
    public void HealthBenchmarkTestsExpandedPipelineDepths(BenchmarkIntensity intensity)
    {
        var profile = BenchmarkProfile.For(intensity);

        Assert.Equal([1, 4, 8, 16, 32, 64], profile.HealthPipelineDepths);
        Assert.True(profile.HealthStatSegments >= profile.HealthPipelineDepths.Max());
    }

    [Fact]
    public void QuickHealthBenchmarkUsesOneFullDepth64Batch()
    {
        var profile = BenchmarkProfile.For(BenchmarkIntensity.Quick);

        Assert.Equal(64, profile.HealthStatSegments);
        Assert.Equal(2, profile.HealthStatRounds);
    }

    [Fact]
    public void HealthRoundTimeoutScalesForLowDepthLatency()
    {
        var minimum = TimeSpan.FromSeconds(4);

        var sequential = UsenetBenchmarkService.ResolveHealthRoundTimeout(
            minimum, latencyMs: 50, requestCount: 65, depth: 1);
        var pipelined = UsenetBenchmarkService.ResolveHealthRoundTimeout(
            minimum, latencyMs: 50, requestCount: 65, depth: 64);

        Assert.True(sequential > minimum);
        Assert.Equal(minimum, pipelined);
    }
}
