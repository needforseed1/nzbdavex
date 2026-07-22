namespace NzbWebDAV.Exceptions;

/// <summary>
/// A health check could not confirm whether one or more articles exist because
/// at least one eligible provider never answered for them. This is an
/// incomplete availability check, not article absence: callers must not treat
/// it as a missing article, trigger repair, or cache the segments as missing.
/// </summary>
public class UsenetArticleUnverifiableException(
    IReadOnlyList<string> segmentIds,
    IReadOnlyList<string> unavailableProviders,
    Exception? innerException = null)
    : Exception(BuildMessage(segmentIds, unavailableProviders), innerException)
{
    public IReadOnlyList<string> SegmentIds { get; } = segmentIds;
    public IReadOnlyList<string> UnavailableProviders { get; } = unavailableProviders;

    private static string BuildMessage(
        IReadOnlyList<string> segmentIds,
        IReadOnlyList<string> unavailableProviders)
    {
        var providers = unavailableProviders.Count > 0
            ? string.Join(", ", unavailableProviders)
            : "unknown";
        return $"Could not verify {segmentIds.Count} article(s): " +
               $"no availability result was received from {providers}. " +
               "Article presence is unconfirmed, not missing.";
    }
}
