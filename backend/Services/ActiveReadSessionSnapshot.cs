using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Services;

/// <summary>
/// Immutable view of one active or terminal playback session. The registry owns
/// all mutable lifecycle state; broadcasters and persistence consume snapshots
/// so request threads cannot change a session while it is being serialized.
/// </summary>
public sealed record ActiveReadSessionSnapshot(
    Guid Id,
    string Path,
    string FileName,
    long? FileSize,
    string? ClientUserAgent,
    string? ClientIp,
    Guid? DavItemId,
    Guid? HistoryItemId,
    ReadSession.EndReasonCode EndReason,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    long BytesRead,
    long CurrentOffset,
    long MaxOffset);
