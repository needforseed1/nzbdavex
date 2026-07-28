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
}
