namespace NzbWebDAV.Exceptions;

/// <summary>
/// A bounded availability sample did not find any provider with complete
/// coverage. This is an admission failure, not proof that a specific article
/// is missing, so callers must not cache a missing segment or attempt repair.
/// </summary>
public sealed class UsenetHealthQualificationException(
    int sampleSize,
    int bestCoverage)
    : Exception(BuildMessage(sampleSize, bestCoverage))
{
    public int SampleSize { get; } = sampleSize;
    public int BestCoverage { get; } = bestCoverage;

    private static string BuildMessage(int sampleSize, int bestCoverage) =>
        $"Health-check qualification failed: no provider returned all " +
        $"{sampleSize} sampled articles (best {bestCoverage}/{sampleSize}). " +
        "The bulk health check was not started.";
}
