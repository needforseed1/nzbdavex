using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory list of currently active WebDAV read sessions, used to surface
/// "what's being read right now and from which backbone" in the UI. No persistence:
/// entries live only while a client is actively pulling bytes.
/// </summary>
public class ActiveReadRegistry
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    // Process-lifetime monotonic counter of every byte served downstream. The
    // broadcaster samples this on a fixed tick to compute a rolling rate, so
    // active (not-yet-pruned) reads still show up in throughput.
    private long _totalBytesServed;
    public long TotalBytesServed => Interlocked.Read(ref _totalBytesServed);

    public Guid GetOrCreate(string path, string clientKey, string fileName, long? fileSize)
    {
        var id = DeriveId(path, clientKey);
        var now = DateTimeOffset.UtcNow;
        _entries.AddOrUpdate(
            id,
            _ => new Entry
            {
                Id = id,
                Path = path,
                FileName = fileName,
                FileSize = fileSize,
                ClientKey = clientKey,
                StartedAt = now,
                LastActivityAt = now,
            },
            (_, existing) =>
            {
                existing.LastActivityAt = now;
                if (fileSize is { } size) existing.FileSize = size;
                return existing;
            });
        return id;
    }

    public void Touch(Guid id, long bytesRead, long? currentOffset = null)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.LastActivityAt = DateTimeOffset.UtcNow;
            if (bytesRead > 0)
            {
                Interlocked.Add(ref entry.BytesRead, bytesRead);
                Interlocked.Add(ref _totalBytesServed, bytesRead);
            }
            if (currentOffset.HasValue)
                Interlocked.Exchange(ref entry.CurrentOffset, currentOffset.Value);
        }
    }

    /// <summary>
    /// Update the user-facing metadata on an existing session. Used once the
    /// real filename/size are resolved from the dav store (the path passed to
    /// GetOrCreate is usually an opaque GUID for .ids/-style paths).
    /// </summary>
    public void UpdateInfo(Guid id, string? fileName, long? fileSize)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        if (!string.IsNullOrWhiteSpace(fileName)) entry.FileName = fileName;
        if (fileSize is { } size) entry.FileSize = size;
    }

    public IReadOnlyList<Entry> Snapshot()
    {
        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        return _entries.Values
            .Where(e => e.LastActivityAt >= cutoff)
            .OrderBy(e => e.StartedAt)
            .ToList();
    }

    /// <summary>
    /// Remove entries that haven't been touched within the activity window.
    /// Returns the pruned entries so callers can clear external bookkeeping
    /// and persist a terminal record of the session.
    /// </summary>
    public IReadOnlyList<Entry> PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - ActivityWindow;
        var expired = _entries
            .Where(kv => kv.Value.LastActivityAt < cutoff)
            .Select(kv => kv.Value)
            .ToList();
        foreach (var entry in expired) _entries.TryRemove(entry.Id, out _);
        return expired;
    }

    public int Count => _entries.Count;

    // (path, clientKey) -> stable Guid so successive range requests from the
    // same player on the same file share a single session id.
    private static Guid DeriveId(string path, string clientKey)
    {
        var bytes = Encoding.UTF8.GetBytes($"{path}\n{clientKey}");
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(bytes, hash);
        return new Guid(hash);
    }

    public sealed class Entry
    {
        public Guid Id { get; init; }
        public string Path { get; init; } = "";
        public string FileName { get; set; } = "";
        public long? FileSize { get; set; }
        public string ClientKey { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastActivityAt { get; set; }
        public long BytesRead;
        /// <summary>
        /// Most recent absolute file offset the player has been served (i.e. the
        /// "where the read head is" position). Updated after every chunk so the
        /// Right Now panel can show genuine playback position, not cumulative
        /// transferred bytes (which over-counts on seek/rewind).
        /// </summary>
        public long CurrentOffset;
    }
}
