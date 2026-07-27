using System.Collections.Concurrent;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory list of currently active WebDAV read sessions, used to surface
/// "what's being read right now and from which backbone" in the UI. No persistence:
/// entries live only while a client is actively pulling bytes.
/// </summary>
public class ActiveReadRegistry
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromSeconds(15);
    private readonly Func<DateTimeOffset> _utcNow;

    // Two indexes. _keyToId dedupes successive range requests from the same
    // (path, clientKey) onto a single session id while the session is active.
    // Each new session gets a fresh Guid so its terminal ReadSession row never
    // collides with a previously-pruned session for the same player and file —
    // a hash-derived id would re-insert a duplicate primary key the second
    // time the same player opens the same file and trip the SQLite UNIQUE
    // constraint on the metrics flush.
    private readonly ConcurrentDictionary<string, Guid> _keyToId = new();
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    // Process-lifetime monotonic counter of every byte served downstream. The
    // broadcaster samples this on a fixed tick to compute a rolling rate, so
    // active (not-yet-pruned) reads still show up in throughput.
    private long _totalBytesServed;
    public long TotalBytesServed => Interlocked.Read(ref _totalBytesServed);

    public ActiveReadRegistry() : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal ActiveReadRegistry(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow;
    }

    public Guid GetOrCreate(string path, string clientKey, string fileName, long? fileSize)
    {
        var key = BuildKey(path, clientKey);
        var now = _utcNow();

        while (true)
        {
            if (_keyToId.TryGetValue(key, out var existingId)
                && _entries.TryGetValue(existingId, out var existing))
            {
                lock (existing.LifetimeLock)
                {
                    if (existing.Removed) continue;
                    existing.LastActivityAt = now;
                    if (fileSize is { } size) existing.FileSize = size;
                    return existingId;
                }
            }

            var newId = Guid.NewGuid();
            var newEntry = new Entry
            {
                Id = newId,
                Path = path,
                FileName = fileName,
                FileSize = fileSize,
                ClientKey = clientKey,
                StartedAt = now,
                LastActivityAt = now,
            };

            if (_keyToId.TryAdd(key, newId))
            {
                _entries[newId] = newEntry;
                return newId;
            }
            // Lost the race against another GetOrCreate for the same key;
            // loop and reuse whichever id the winner published.
        }
    }

    public void Touch(Guid id, long bytesRead, long? currentOffset = null)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            lock (entry.LifetimeLock)
            {
                if (entry.Removed) return;
                entry.LastActivityAt = _utcNow();
                if (bytesRead > 0)
                {
                    Interlocked.Add(ref entry.BytesRead, bytesRead);
                    Interlocked.Add(ref _totalBytesServed, bytesRead);
                }
                if (currentOffset.HasValue)
                {
                    Interlocked.Exchange(ref entry.CurrentOffset, currentOffset.Value);
                    UpdateMaximum(ref entry.MaxOffset, currentOffset.Value);
                }
            }
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
        lock (entry.LifetimeLock)
        {
            if (entry.Removed) return;
            if (!string.IsNullOrWhiteSpace(fileName)) entry.FileName = fileName;
            if (fileSize is { } size) entry.FileSize = size;
        }
    }

    /// <summary>
    /// Attach the content identity behind an opaque playback path, so the
    /// persisted session can be tied back to its dav item and grab history.
    /// </summary>
    public void UpdateContentIds(Guid id, Guid? davItemId, Guid? historyItemId)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        lock (entry.LifetimeLock)
        {
            if (entry.Removed) return;
            if (davItemId.HasValue) entry.DavItemId = davItemId;
            if (historyItemId.HasValue) entry.HistoryItemId = historyItemId;
        }
    }

    public void MarkRequestStarted(Guid id, string? clientIp, string? clientUserAgent)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        lock (entry.LifetimeLock)
        {
            if (entry.Removed) return;
            entry.LastActivityAt = _utcNow();
            entry.ClientIp = clientIp;
            entry.ClientUserAgent = clientUserAgent;
            Interlocked.Increment(ref entry.OpenRequests);
            // A seek commonly cancels one range and immediately starts another.
            // The newest request therefore owns the eventual terminal reason.
            entry.EndReason = ReadSession.EndReasonCode.Completed;
        }
    }

    public void MarkRequestEnded(Guid id, ReadSession.EndReasonCode endReason)
    {
        if (!_entries.TryGetValue(id, out var entry)) return;
        lock (entry.LifetimeLock)
        {
            if (entry.Removed) return;
            entry.LastActivityAt = _utcNow();
            entry.EndReason = endReason;
            DecrementNonNegative(ref entry.OpenRequests);
        }
    }

    public IReadOnlyList<ActiveReadSessionSnapshot> Snapshot()
    {
        var cutoff = _utcNow() - ActivityWindow;
        var active = new List<ActiveReadSessionSnapshot>();
        foreach (var entry in _entries.Values)
        {
            lock (entry.LifetimeLock)
            {
                if (!entry.Removed &&
                    (entry.LastActivityAt >= cutoff || entry.IsRequestOpen))
                    active.Add(CreateSnapshot(entry));
            }
        }
        return active.OrderBy(e => e.StartedAt).ToList();
    }

    /// <summary>
    /// Remove entries that haven't been touched within the activity window and
    /// have no HTTP request still open. Returns the pruned entries so callers
    /// can clear external bookkeeping and persist a terminal record of the
    /// session.
    /// </summary>
    public IReadOnlyList<ActiveReadSessionSnapshot> PruneExpired()
    {
        var cutoff = _utcNow() - ActivityWindow;
        var expired = new List<ActiveReadSessionSnapshot>();
        foreach (var entry in _entries.Values)
        {
            lock (entry.LifetimeLock)
            {
                // Re-check under the same lock used by Touch and request start.
                // A range that resumes while pruning is being decided therefore
                // wins cleanly instead of having its refreshed session removed.
                if (entry.Removed || entry.IsRequestOpen || entry.LastActivityAt >= cutoff)
                    continue;
                entry.Removed = true;
                var key = BuildKey(entry.Path, entry.ClientKey);
                ((ICollection<KeyValuePair<string, Guid>>)_keyToId)
                    .Remove(new KeyValuePair<string, Guid>(key, entry.Id));
                if (_entries.TryRemove(entry.Id, out _))
                    expired.Add(CreateSnapshot(entry));
            }
        }
        return expired;
    }

    /// <summary>
    /// Remove and return every entry regardless of activity. Used on shutdown so
    /// reads still in flight are persisted instead of vanishing. Draining in one
    /// pass keeps a session from being returned twice and colliding on its
    /// primary key.
    /// </summary>
    public IReadOnlyList<ActiveReadSessionSnapshot> DrainAll()
    {
        var all = _entries.Values.ToList();
        var drained = new List<ActiveReadSessionSnapshot>(all.Count);
        foreach (var entry in all)
        {
            lock (entry.LifetimeLock)
            {
                if (entry.Removed) continue;
                entry.Removed = true;
                var key = BuildKey(entry.Path, entry.ClientKey);
                ((ICollection<KeyValuePair<string, Guid>>)_keyToId)
                    .Remove(new KeyValuePair<string, Guid>(key, entry.Id));
                if (_entries.TryRemove(entry.Id, out _))
                    drained.Add(CreateSnapshot(entry));
            }
        }
        return drained;
    }

    public int Count => _entries.Count;

    private static string BuildKey(string path, string clientKey) => path + "\n" + clientKey;

    private static ActiveReadSessionSnapshot CreateSnapshot(Entry entry) =>
        new(
            entry.Id,
            entry.Path,
            entry.FileName,
            entry.FileSize,
            entry.ClientUserAgent,
            entry.ClientIp,
            entry.DavItemId,
            entry.HistoryItemId,
            entry.EndReason,
            entry.StartedAt,
            entry.LastActivityAt,
            Interlocked.Read(ref entry.BytesRead),
            Interlocked.Read(ref entry.CurrentOffset),
            Interlocked.Read(ref entry.MaxOffset));

    private static void UpdateMaximum(ref long target, long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (candidate <= current) return;
            if (Interlocked.CompareExchange(ref target, candidate, current) == current) return;
        }
    }

    private static void DecrementNonNegative(ref int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref value);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref value, current - 1, current) == current) return;
        }
    }

    private sealed class Entry
    {
        public Guid Id { get; init; }
        public string Path { get; init; } = "";
        public string FileName { get; set; } = "";
        public long? FileSize { get; set; }
        public string ClientKey { get; init; } = "";
        public string? ClientUserAgent { get; set; }
        public string? ClientIp { get; set; }
        public Guid? DavItemId { get; set; }
        public Guid? HistoryItemId { get; set; }
        public ReadSession.EndReasonCode EndReason { get; set; } =
            ReadSession.EndReasonCode.Completed;
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset LastActivityAt { get; set; }
        /// <summary>
        /// HTTP requests currently streaming from this session. A session with
        /// one open request is alive by definition, however long it has been
        /// since it last moved a byte.
        /// </summary>
        public int OpenRequests;
        public bool IsRequestOpen => Volatile.Read(ref OpenRequests) > 0;
        internal object LifetimeLock { get; } = new();
        internal bool Removed;
        public long BytesRead;
        /// <summary>
        /// Most recent absolute file offset the player has been served (i.e. the
        /// "where the read head is" position). Updated after every chunk so the
        /// Right Now panel can show genuine playback position, not cumulative
        /// transferred bytes (which over-counts on seek/rewind).
        /// </summary>
        public long CurrentOffset;
        /// <summary>
        /// Furthest offset reached during the session. Unlike CurrentOffset it
        /// never rewinds on a seek, so it approximates how far into the file the
        /// player actually got.
        /// </summary>
        public long MaxOffset;
    }
}
