using System.Collections.Concurrent;

namespace NzbWebDAV.Services;

/// <summary>
/// Accumulates per-request playback diagnostics into per-session totals.
///
/// One read session spans many HTTP requests — every seek cancels one range
/// request and opens another — so the counters a single request carries are
/// only a fragment of what the user experienced. Requests fold their totals in
/// when they finish; the terminal ReadSession row takes them when the session
/// is pruned.
/// </summary>
public class PlaybackSessionStats
{
    private readonly ConcurrentDictionary<Guid, Accumulator> _sessions = new();

    public int Count => _sessions.Count;

    public void Fold(Guid sessionId, PlaybackRequestDelta delta)
    {
        var accumulator = _sessions.GetOrAdd(sessionId, _ => new Accumulator());
        accumulator.Fold(delta);
    }

    /// <summary>
    /// Records a stall the moment it happens rather than when its request ends.
    /// A sequential stream is one long request, so folding stalls at completion
    /// would leave a live view showing zero while the viewer sits buffering.
    /// </summary>
    public void RecordStall(Guid sessionId, bool isUpstream, long elapsedMs)
    {
        var accumulator = _sessions.GetOrAdd(sessionId, _ => new Accumulator());
        accumulator.RecordWait(
            isUpstream,
            elapsedMs,
            elapsedMs,
            isNewWait: true,
            headOfLine: false);
        if (isUpstream) accumulator.RecordCompletedUpstreamWait(elapsedMs);
    }

    /// <summary>
    /// One instalment of a wait. A wait still in progress is reported
    /// repeatedly, so <paramref name="deltaMs"/> is what is new since the last
    /// report while <paramref name="totalElapsedMs"/> is how long it has run in
    /// all; only the first instalment counts as a wait.
    /// </summary>
    public void RecordWait(
        Guid sessionId,
        bool isUpstream,
        long deltaMs,
        long totalElapsedMs,
        bool isNewWait,
        bool headOfLine = false)
    {
        var accumulator = _sessions.GetOrAdd(sessionId, _ => new Accumulator());
        accumulator.RecordWait(isUpstream, deltaMs, totalElapsedMs, isNewWait, headOfLine);
    }

    public void BeginWait(Guid sessionId, bool isUpstream, long elapsedMs = 0)
    {
        var accumulator = _sessions.GetOrAdd(sessionId, _ => new Accumulator());
        accumulator.BeginWait(isUpstream, elapsedMs);
    }

    public void EndWait(Guid sessionId, bool isUpstream)
    {
        if (_sessions.TryGetValue(sessionId, out var accumulator))
            accumulator.EndWait(isUpstream);
    }

    public void RecordZeroFill(Guid sessionId, long bytes)
    {
        var accumulator = _sessions.GetOrAdd(sessionId, _ => new Accumulator());
        accumulator.RecordZeroFill(bytes);
    }

    public void RecordBodyStallRecovery(Guid sessionId)
    {
        var accumulator = _sessions.GetOrAdd(sessionId, _ => new Accumulator());
        accumulator.RecordBodyStallRecovery();
    }

    public PlaybackSessionTotals? Peek(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var accumulator) ? accumulator.Snapshot() : null;

    /// <summary>Snapshot and forget. Called once when the session is pruned.</summary>
    public PlaybackSessionTotals? Take(Guid sessionId) =>
        _sessions.TryRemove(sessionId, out var accumulator) ? accumulator.Snapshot() : null;

    public void Drop(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>
    /// Safety net for accumulators whose session never reached the prune path
    /// (an unregistered session id, or a crash between fold and prune).
    /// </summary>
    public int DropStale(
        TimeSpan olderThan,
        IReadOnlySet<Guid>? activeSessionIds = null)
    {
        var cutoff = DateTimeOffset.UtcNow - olderThan;
        var dropped = 0;
        foreach (var (id, accumulator) in _sessions)
        {
            if (activeSessionIds?.Contains(id) == true) continue;
            if (!accumulator.IsOlderThan(cutoff)) continue;
            if (_sessions.TryRemove(id, out _)) dropped++;
        }
        return dropped;
    }

    private sealed class Accumulator
    {
        private readonly object _lock = new();
        private readonly PlaybackMetricsAccumulator _metrics = new();
        private int _requestCount;
        private long? _firstByteMs;
        private long _maxOffset;
        private DateTimeOffset? _firstRequestStartedAt;
        private string? _errorNote;
        private int _activeUpstreamWallWaits;
        private long? _upstreamWallWaitStartedAtMs;
        private readonly List<PlaybackWaitWindow> _upstreamWaitWindows = [];

        public DateTimeOffset LastFoldAt { get; private set; } = DateTimeOffset.UtcNow;

        public bool IsOlderThan(DateTimeOffset cutoff)
        {
            lock (_lock)
                return LastFoldAt < cutoff;
        }

        public void RecordWait(
            bool isUpstream,
            long deltaMs,
            long totalElapsedMs,
            bool isNewWait,
            bool headOfLine)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _metrics.RecordWait(
                    isUpstream,
                    deltaMs,
                    totalElapsedMs,
                    isNewWait,
                    headOfLine);
            }
        }

        public void BeginWait(bool isUpstream, long elapsedMs)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _metrics.BeginWait(isUpstream);
                if (!isUpstream) return;

                var startedAt = LastFoldAt.ToUnixTimeMilliseconds()
                                - Math.Max(0, elapsedMs);
                if (_activeUpstreamWallWaits == 0)
                    _upstreamWallWaitStartedAtMs = startedAt;
                else if (_upstreamWallWaitStartedAtMs is { } current)
                    _upstreamWallWaitStartedAtMs = Math.Min(current, startedAt);
                _activeUpstreamWallWaits++;
            }
        }

        public void EndWait(bool isUpstream)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _metrics.EndWait(isUpstream);
                if (!isUpstream || _activeUpstreamWallWaits <= 0) return;

                _activeUpstreamWallWaits--;
                if (_activeUpstreamWallWaits > 0) return;
                AddUpstreamWaitWindow(
                    _upstreamWallWaitStartedAtMs
                    ?? LastFoldAt.ToUnixTimeMilliseconds(),
                    LastFoldAt.ToUnixTimeMilliseconds());
                _upstreamWallWaitStartedAtMs = null;
            }
        }

        public void RecordCompletedUpstreamWait(long elapsedMs)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                var endedAt = LastFoldAt.ToUnixTimeMilliseconds();
                AddUpstreamWaitWindow(endedAt - Math.Max(0, elapsedMs), endedAt);
            }
        }

        public void RecordZeroFill(long bytes)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _metrics.RecordZeroFill(bytes);
            }
        }

        public void RecordBodyStallRecovery()
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _metrics.RecordBodyStallRecovery();
            }
        }

        public void Fold(PlaybackRequestDelta delta)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _requestCount++;

                // Startup latency: the first-byte time of the request that
                // started first, which is the wait the viewer sat through before
                // anything played. Requests finish out of order and a mid-play
                // seek off a warm stream answers in tens of milliseconds, so
                // neither the smallest value nor the last one folded is it.
                if (delta.FirstByteMs is { } firstByte &&
                    (_firstRequestStartedAt is null ||
                     delta.RequestStartedAt <= _firstRequestStartedAt))
                {
                    _firstByteMs = firstByte;
                    _firstRequestStartedAt = delta.RequestStartedAt;
                }

                _maxOffset = Math.Max(_maxOffset, delta.MaxOffset);
                // Stalls are not folded here: RecordStall already counted them as
                // they happened, and folding the request's copy would double them.
                _metrics.Fold(delta);
                if (!string.IsNullOrWhiteSpace(delta.ErrorNote)) _errorNote = delta.ErrorNote;
            }
        }

        public PlaybackSessionTotals Snapshot()
        {
            lock (_lock)
            {
                var metrics = _metrics.Snapshot();
                var waitWindows = SnapshotUpstreamWaitWindows();
                return new PlaybackSessionTotals(
                    _requestCount,
                    _firstByteMs,
                    _maxOffset,
                    metrics.UpstreamStalls,
                    metrics.MaxUpstreamStallMs,
                    metrics.TotalUpstreamStallMs,
                    waitWindows.Sum(window => window.EndedAtMs - window.StartedAtMs),
                    waitWindows.Count == 0
                        ? 0
                        : waitWindows.Max(window => window.EndedAtMs - window.StartedAtMs),
                    waitWindows,
                    metrics.HeadOfLineStalls,
                    metrics.TotalHeadOfLineStallMs,
                    metrics.ActiveUpstreamWaits,
                    metrics.DownstreamStalls,
                    metrics.MaxDownstreamStallMs,
                    metrics.TotalDownstreamStallMs,
                    metrics.ActiveDownstreamWaits,
                    metrics.FallbackRescues,
                    metrics.ProviderRotations,
                    metrics.FallbackBudgetExhaustions,
                    metrics.CacheHits,
                    metrics.CacheMisses,
                    metrics.ConnectionPermitWaits,
                    metrics.MaxConnectionPermitWaitMs,
                    metrics.ProviderPoolWaits,
                    metrics.MaxProviderPoolWaitMs,
                    metrics.ZeroFilledSegments,
                    metrics.ZeroFilledBytes,
                    metrics.BodyStallRecoveries,
                    metrics.BackupProviders,
                    _errorNote);
            }
        }

        private List<PlaybackWaitWindow> SnapshotUpstreamWaitWindows()
        {
            var windows = _upstreamWaitWindows.ToList();
            if (_activeUpstreamWallWaits <= 0 ||
                _upstreamWallWaitStartedAtMs is not { } startedAt)
                return windows;

            AddOrMerge(
                windows,
                new PlaybackWaitWindow(
                    startedAt,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return windows;
        }

        private void AddUpstreamWaitWindow(long startedAt, long endedAt) =>
            AddOrMerge(
                _upstreamWaitWindows,
                new PlaybackWaitWindow(
                    Math.Min(startedAt, endedAt),
                    Math.Max(startedAt, endedAt)));

        private static void AddOrMerge(
            List<PlaybackWaitWindow> windows,
            PlaybackWaitWindow next)
        {
            if (windows.Count == 0 || next.StartedAtMs > windows[^1].EndedAtMs)
            {
                windows.Add(next);
                return;
            }

            var previous = windows[^1];
            windows[^1] = new PlaybackWaitWindow(
                Math.Min(previous.StartedAtMs, next.StartedAtMs),
                Math.Max(previous.EndedAtMs, next.EndedAtMs));
        }
    }
}
