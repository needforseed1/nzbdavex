namespace UsenetSharp.Exceptions;

/// <summary>
/// The byte source behind a decoded stream went quiet for longer than its armed
/// inactivity window. Distinct from a generic timeout so callers can tell "this
/// transfer stopped" apart from "this command never answered".
/// </summary>
public class ReadInactivityTimeoutException : TimeoutException
{
    public ReadInactivityTimeoutException(
        string errorMessage,
        Exception? innerException = null,
        long transferredBytes = 0)
        : base(errorMessage, innerException)
    {
        TransferredBytes = Math.Max(0, transferredBytes);
    }

    /// <summary>
    /// Bytes already received before the source went silent. The transport-level
    /// pipelined reader uses encoded bytes; the decoded stream watchdog reports
    /// decoded bytes.
    /// </summary>
    public long TransferredBytes { get; }
}
