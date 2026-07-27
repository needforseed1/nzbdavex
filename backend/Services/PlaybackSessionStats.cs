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
        RecordWait(sessionId, isUpstream, elapsedMs, elapsedMs, isNewWait: true, headOfLine: false);
        EndWait(sessionId, isUpstream);
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

    public void BeginWait(Guid sessionId, bool isUpstream)
    {
        var accumulator = _sessions.GetOrAdd(sessionId, _ => new Accumulator());
        accumulator.BeginWait(isUpstream);
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
        private readonly Dictionary<string, BackupTotals> _backups = new(StringComparer.OrdinalIgnoreCase);
        private int _requestCount;
        private long? _firstByteMs;
        private long _maxOffset;
        private int _upstreamStalls;
        private long _maxUpstreamStallMs;
        private long _totalUpstreamStallMs;
        private int _headOfLineStalls;
        private long _totalHeadOfLineStallMs;
        private int _activeUpstreamWaits;
        private int _downstreamStalls;
        private long _maxDownstreamStallMs;
        private long _totalDownstreamStallMs;
        private int _activeDownstreamWaits;
        private int _fallbackRescues;
        private int _providerRotations;
        private int _fallbackBudgetExhaustions;
        private int _cacheHits;
        private int _cacheMisses;
        private int _connectionPermitWaits;
        private long _maxConnectionPermitWaitMs;
        private int _providerPoolWaits;
        private long _maxProviderPoolWaitMs;
        private int _zeroFilledSegments;
        private long _zeroFilledBytes;
        private int _bodyStallRecoveries;
        private DateTimeOffset? _firstRequestStartedAt;
        private string? _errorNote;

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
                if (isUpstream)
                {
                    if (isNewWait) _upstreamStalls++;
                    if (isNewWait && headOfLine) _headOfLineStalls++;
                    if (headOfLine) _totalHeadOfLineStallMs += Math.Max(0, deltaMs);
                    _maxUpstreamStallMs = Math.Max(_maxUpstreamStallMs, totalElapsedMs);
                    _totalUpstreamStallMs += Math.Max(0, deltaMs);
                }
                else
                {
                    if (isNewWait) _downstreamStalls++;
                    _maxDownstreamStallMs = Math.Max(_maxDownstreamStallMs, totalElapsedMs);
                    _totalDownstreamStallMs += Math.Max(0, deltaMs);
                }
            }
        }

        public void BeginWait(bool isUpstream)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                if (isUpstream)
                    _activeUpstreamWaits++;
                else
                    _activeDownstreamWaits++;
            }
        }

        public void EndWait(bool isUpstream)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                if (isUpstream)
                    DecrementNonNegative(ref _activeUpstreamWaits);
                else
                    DecrementNonNegative(ref _activeDownstreamWaits);
            }
        }

        public void RecordZeroFill(long bytes)
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _zeroFilledSegments++;
                _zeroFilledBytes += Math.Max(0, bytes);
            }
        }

        public void RecordBodyStallRecovery()
        {
            lock (_lock)
            {
                LastFoldAt = DateTimeOffset.UtcNow;
                _bodyStallRecoveries++;
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
                _fallbackRescues += delta.FallbackRescues;
                _providerRotations += delta.ProviderRotations;
                _fallbackBudgetExhaustions += delta.FallbackBudgetExhaustions;
                _cacheHits += delta.CacheHits;
                _cacheMisses += delta.CacheMisses;
                _connectionPermitWaits += delta.ConnectionPermitWaits;
                _maxConnectionPermitWaitMs =
                    Math.Max(_maxConnectionPermitWaitMs, delta.MaxConnectionPermitWaitMs);
                _providerPoolWaits += delta.ProviderPoolWaits;
                _maxProviderPoolWaitMs =
                    Math.Max(_maxProviderPoolWaitMs, delta.MaxProviderPoolWaitMs);
                _zeroFilledSegments += delta.ZeroFilledSegments;
                _zeroFilledBytes += delta.ZeroFilledBytes;
                _bodyStallRecoveries += delta.BodyStallRecoveries;
                if (!string.IsNullOrWhiteSpace(delta.ErrorNote)) _errorNote = delta.ErrorNote;

                foreach (var backup in delta.BackupProviders)
                {
                    if (string.IsNullOrWhiteSpace(backup.ProviderId)) continue;
                    if (_backups.TryGetValue(backup.ProviderId, out var current))
                    {
                        _backups[backup.ProviderId] = new BackupTotals(
                            string.IsNullOrWhiteSpace(current.Host) ? backup.Host : current.Host,
                            current.Attempts + backup.Attempts,
                            current.Rescued + backup.Rescued,
                            current.Missing + backup.Missing,
                            current.Timeouts + backup.Timeouts,
                            current.Errors + backup.Errors);
                    }
                    else
                    {
                        _backups[backup.ProviderId] = new BackupTotals(
                            backup.Host,
                            backup.Attempts,
                            backup.Rescued,
                            backup.Missing,
                            backup.Timeouts,
                            backup.Errors);
                    }
                }
            }
        }

        public PlaybackSessionTotals Snapshot()
        {
            lock (_lock)
            {
                return new PlaybackSessionTotals(
                    _requestCount,
                    _firstByteMs,
                    _maxOffset,
                    _upstreamStalls,
                    _maxUpstreamStallMs,
                    _totalUpstreamStallMs,
                    _headOfLineStalls,
                    _totalHeadOfLineStallMs,
                    _activeUpstreamWaits,
                    _downstreamStalls,
                    _maxDownstreamStallMs,
                    _totalDownstreamStallMs,
                    _activeDownstreamWaits,
                    _fallbackRescues,
                    _providerRotations,
                    _fallbackBudgetExhaustions,
                    _cacheHits,
                    _cacheMisses,
                    _connectionPermitWaits,
                    _maxConnectionPermitWaitMs,
                    _providerPoolWaits,
                    _maxProviderPoolWaitMs,
                    _zeroFilledSegments,
                    _zeroFilledBytes,
                    _bodyStallRecoveries,
                    _backups
                        .Select(x => new PlaybackBackupProviderStat(
                            x.Key,
                            x.Value.Host,
                            x.Value.Attempts,
                            x.Value.Rescued,
                            x.Value.Missing,
                            x.Value.Timeouts,
                            x.Value.Errors))
                        .OrderByDescending(x => x.Rescued)
                        .ThenBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    _errorNote);
            }
        }

        private static void DecrementNonNegative(ref int value)
        {
            if (value > 0) value--;
        }

        private readonly record struct BackupTotals(
            string Host,
            long Attempts,
            long Rescued,
            long Missing,
            long Timeouts,
            long Errors);
    }
}

/// <summary>
/// Durable per-provider breakdown of one playback session: how many segments and
/// bytes each provider served, plus the backup-attempt outcomes. Stored as JSON on
/// the session row because raw SegmentFetches expire long before the session does.
/// Provider ids only — hosts and nicknames are resolved from live config at render
/// time so a rename retroactively fixes old rows.
/// </summary>
public sealed record PlaybackProviderStat(
    string ProviderId,
    long Segments,
    long Bytes,
    long Attempts,
    long Rescued,
    long Missing,
    long Timeouts,
    long Errors,
    bool IsBackup);

/// <summary>Per-backup-provider activity observed during one request or session.</summary>
public sealed record PlaybackBackupProviderStat(
    string ProviderId,
    string Host,
    long Attempts,
    long Rescued,
    long Missing,
    long Timeouts,
    long Errors);

/// <summary>
/// What a single finished HTTP playback request contributes to its session.
/// Stalls are absent by design: they are reported through
/// <see cref="PlaybackSessionStats.RecordStall"/> as they happen, so a live
/// view does not have to wait for the request to end.
/// </summary>
public sealed record PlaybackRequestDelta(
    /// When the request began. Requests complete out of order, so this is what
    /// identifies the one whose first byte was the viewer's startup wait.
    DateTimeOffset RequestStartedAt,
    long? FirstByteMs,
    long MaxOffset,
    int FallbackRescues,
    int ProviderRotations,
    int FallbackBudgetExhaustions,
    int CacheHits,
    int CacheMisses,
    int ConnectionPermitWaits,
    long MaxConnectionPermitWaitMs,
    int ProviderPoolWaits,
    long MaxProviderPoolWaitMs,
    int ZeroFilledSegments,
    long ZeroFilledBytes,
    int BodyStallRecoveries,
    IReadOnlyList<PlaybackBackupProviderStat> BackupProviders,
    string? ErrorNote);

public sealed record PlaybackSessionTotals(
    int RequestCount,
    long? FirstByteMs,
    long MaxOffset,
    int UpstreamStalls,
    long MaxUpstreamStallMs,
    long TotalUpstreamStallMs,
    /// <summary>
    /// Upstream waits that had already-downloaded segments queued behind the one
    /// the reader needed. Distinguishes "the source is too slow" from "one slow
    /// article is blocking a full buffer" — opposite problems, opposite fixes.
    /// </summary>
    int HeadOfLineStalls,
    long TotalHeadOfLineStallMs,
    int ActiveUpstreamWaits,
    int DownstreamStalls,
    long MaxDownstreamStallMs,
    long TotalDownstreamStallMs,
    int ActiveDownstreamWaits,
    int FallbackRescues,
    int ProviderRotations,
    int FallbackBudgetExhaustions,
    int CacheHits,
    int CacheMisses,
    int ConnectionPermitWaits,
    long MaxConnectionPermitWaitMs,
    int ProviderPoolWaits,
    long MaxProviderPoolWaitMs,
    int ZeroFilledSegments,
    long ZeroFilledBytes,
    int BodyStallRecoveries,
    IReadOnlyList<PlaybackBackupProviderStat> BackupProviders,
    string? ErrorNote);
