namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Carries the playback body-silence deadline through the NNTP wrapper stack to
/// the transport that is actually reading bytes from the socket.
/// </summary>
internal sealed record BodyReadInactivityContext(TimeSpan Timeout);
