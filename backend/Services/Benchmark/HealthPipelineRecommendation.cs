namespace NzbWebDAV.Services.Benchmark;

public static class HealthPipelineRecommendation
{
    private const double NearBestFraction = 0.95;

    public static int? Select(IReadOnlyCollection<BenchmarkHealthPipeliningPoint> points)
    {
        var reliable = points
            .Where(x => x.Reliable && x.StatsPerSecond > 0)
            .ToList();
        if (reliable.Count == 0) return null;

        var nearBestRate = reliable.Max(x => x.StatsPerSecond) * NearBestFraction;
        return reliable
            .Where(x => x.StatsPerSecond >= nearBestRate)
            .Min(x => x.Depth);
    }
}
