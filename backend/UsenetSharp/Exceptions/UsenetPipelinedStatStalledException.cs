namespace UsenetSharp.Exceptions;

/// <summary>
/// An established NNTP connection stopped producing responses partway through
/// a pipelined STAT batch. Responses read before the stall remain valid and are
/// yielded before this exception reaches the caller.
/// </summary>
public sealed class UsenetPipelinedStatStalledException(
    string message,
    int receivedResponses,
    Exception? innerException = null)
    : TimeoutException(message, innerException)
{
    public int ReceivedResponses { get; } = Math.Max(0, receivedResponses);
}
