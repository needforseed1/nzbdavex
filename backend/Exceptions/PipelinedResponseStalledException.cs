namespace NzbWebDAV.Exceptions;

/// <summary>
/// A pipelined command stopped producing responses on an otherwise established
/// connection. This is a stalled socket rather than an unhealthy provider: the
/// same provider usually keeps answering normally on its other connections, so
/// callers should rotate the socket without holding it against the provider.
/// </summary>
public sealed class PipelinedResponseStalledException(
    string message,
    int receivedResponses,
    Exception? innerException = null)
    : TimeoutException(message, innerException)
{
    /// <summary>
    /// Responses received before the silence began. A value above zero proves
    /// the connection was serving this batch before it stalled.
    /// </summary>
    public int ReceivedResponses { get; } = receivedResponses;
}
