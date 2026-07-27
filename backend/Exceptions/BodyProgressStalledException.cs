namespace NzbWebDAV.Exceptions;

/// <summary>
/// A body that had already started transferring stopped producing bytes. The
/// provider answered and the socket stayed open, so this is a wedged connection
/// rather than an unhealthy provider: rotate the socket and refetch the segment
/// without holding it against the provider.
/// </summary>
public sealed class BodyProgressStalledException(
    string message,
    long transferredBytes,
    string? providerId = null,
    string? providerHost = null,
    Exception? innerException = null)
    : TimeoutException(message, innerException)
{
    /// <summary>
    /// Bytes delivered before the silence began. A value above zero proves the
    /// connection was serving this body before it stalled.
    /// </summary>
    public long TransferredBytes { get; } = transferredBytes;

    public string? ProviderId { get; } = providerId;

    public string? ProviderHost { get; } = providerHost;
}
