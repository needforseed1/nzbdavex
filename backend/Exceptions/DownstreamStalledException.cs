namespace NzbWebDAV.Exceptions;

/// <summary>
/// A client stopped accepting data and never came back. Distinct from a client
/// abort, which is the player politely closing the connection: here the socket
/// stays open and nothing below us resolves it, so the request would hold its
/// buffered segments and provider connections indefinitely.
///
/// Not an upstream failure. Reported separately so the session records that the
/// viewer's side went away rather than blaming the source.
/// </summary>
public sealed class DownstreamStalledException(
    string message,
    long offset,
    long stalledMs
) : Exception(message)
{
    /// <summary>File offset the client stopped reading at.</summary>
    public long Offset { get; } = offset;

    /// <summary>How long the final write waited before the request was abandoned.</summary>
    public long StalledMs { get; } = stalledMs;
}
