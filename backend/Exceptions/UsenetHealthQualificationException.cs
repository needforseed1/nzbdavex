namespace NzbWebDAV.Exceptions;

/// <summary>
/// A bounded availability sample was not collectively covered by the available
/// providers. This is an admission failure, not proof that a specific article
/// is missing, so callers must not cache a missing segment or attempt repair.
/// </summary>
public sealed class UsenetHealthQualificationException(
    int sampleSize,
    int aggregateCoverage)
    : Exception(BuildMessage(sampleSize, aggregateCoverage))
{
    public int SampleSize { get; } = sampleSize;
    public int AggregateCoverage { get; } = aggregateCoverage;

    private static string BuildMessage(int sampleSize, int aggregateCoverage) =>
        $"Health-check qualification failed: the available providers collectively returned " +
        $"{aggregateCoverage}/{sampleSize} sampled articles. " +
        "The bulk health check was not started.";
}
