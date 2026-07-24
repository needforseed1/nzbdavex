using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Correlates one HTTP media request with the background segment work spawned
/// while its stream is being constructed and consumed. AsyncLocal is used only
/// to propagate the request object; all mutable counters on that object are
/// thread-safe because buffered segment downloads run concurrently.
/// </summary>
internal static class PlaybackDiagnosticContext
{
    private static readonly AsyncLocal<PlaybackRequestDiagnostics?> CurrentScope = new();

    public static PlaybackRequestDiagnostics? Current => CurrentScope.Value;

    public static IDisposable Begin(PlaybackRequestDiagnostics diagnostics)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = diagnostics;
        return new Scope(() => CurrentScope.Value = previous);
    }

    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed class PlaybackRequestDiagnostics
{
    private static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StallLogInterval = TimeSpan.FromSeconds(10);
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private readonly TimeSpan _stallThreshold;
    private readonly object _stallLogLock = new();
    private readonly ConcurrentDictionary<string, BackupProviderActivity> _backupProviders =
        new(StringComparer.OrdinalIgnoreCase);
    private string _fileName;
    private long? _fileSize;
    private long _streamOpenMs = -1;
    private long _firstByteMs = -1;
    private long _bytesServed;
    private long _currentOffset;
    private int _inFlightSegments;
    private int _bufferedSegments;
    private int _upstreamStalls;
    private int _downstreamStalls;
    private long _maxUpstreamStallMs;
    private long _maxDownstreamStallMs;
    private long _lastUpstreamStallLogMs = long.MinValue;
    private long _lastDownstreamStallLogMs = long.MinValue;
    private int _fallbackRescues;
    private int _providerRotations;
    private int _fallbackBudgetExhaustions;
    private int _cacheHits;
    private int _cacheMisses;
    private int _connectionPermitWaits;
    private long _maxConnectionPermitWaitMs;
    private int _connectionPermitWaitLogged;
    private int _providerPoolWaits;
    private long _maxProviderPoolWaitMs;
    private int _providerPoolWaitLogged;
    private int _completed;

    public PlaybackRequestDiagnostics(
        Guid sessionId,
        string path,
        string fileName,
        string? requestedRange,
        long initialOffset = 0,
        TimeSpan? stallThreshold = null)
    {
        SessionId = sessionId;
        RequestId = Guid.NewGuid();
        Path = path;
        _fileName = fileName;
        RequestedRange = string.IsNullOrWhiteSpace(requestedRange) ? "full" : requestedRange;
        _currentOffset = Math.Max(0, initialOffset);
        _stallThreshold = stallThreshold ?? DefaultStallThreshold;

        Log.Information(
            "playback-session session={SessionId} request={RequestId} stage=request-start " +
            "file={File} path={Path} range={Range}",
            SessionId, RequestId, _fileName, Path, RequestedRange);
    }

    public Guid SessionId { get; }
    public Guid RequestId { get; }
    public string Path { get; }
    public string RequestedRange { get; }
    public long CurrentOffset => Interlocked.Read(ref _currentOffset);

    public void MarkStreamOpened(string fileName, long? fileSize, long effectiveStart)
    {
        if (!string.IsNullOrWhiteSpace(fileName)) _fileName = fileName;
        _fileSize = fileSize;
        Interlocked.Exchange(ref _currentOffset, Math.Max(0, effectiveStart));
        Interlocked.CompareExchange(ref _streamOpenMs, _lifetime.ElapsedMilliseconds, -1);
        Log.Debug(
            "playback-session session={SessionId} request={RequestId} stage=stream-open " +
            "file={File} size={FileSize} offset={Offset} openMs={OpenMs}",
            SessionId, RequestId, _fileName, _fileSize, effectiveStart, _streamOpenMs);
    }

    public void RecordTransfer(int bytes, long position, long upstreamReadMs, long downstreamWriteMs)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _bytesServed, bytes);
        Interlocked.Exchange(ref _currentOffset, position);

        if (Interlocked.CompareExchange(
                ref _firstByteMs, _lifetime.ElapsedMilliseconds, -1) == -1)
        {
            var buffer = BufferSnapshot();
            Log.Information(
                "playback-session session={SessionId} request={RequestId} stage=first-byte " +
                "file={File} offset={Offset} firstByteMs={FirstByteMs} openMs={OpenMs} " +
                "upstreamReadMs={UpstreamReadMs} downstreamWriteMs={DownstreamWriteMs} " +
                "bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
                SessionId, RequestId, _fileName, position - bytes, _firstByteMs,
                Interlocked.Read(ref _streamOpenMs), upstreamReadMs, downstreamWriteMs,
                buffer.BufferedSegments, buffer.InFlightSegments);
        }

        if (upstreamReadMs >= _stallThreshold.TotalMilliseconds)
            RecordStall("upstream-read", upstreamReadMs, position - bytes);
        if (downstreamWriteMs >= _stallThreshold.TotalMilliseconds)
            RecordStall("downstream-write", downstreamWriteMs, position - bytes);
    }

    public void UpstreamOperationStarted() =>
        Interlocked.Increment(ref _inFlightSegments);

    public void UpstreamOperationCompleted() =>
        DecrementNonNegative(ref _inFlightSegments);

    public void SegmentBuffered() =>
        Interlocked.Increment(ref _bufferedSegments);

    public void SegmentDequeued() =>
        DecrementNonNegative(ref _bufferedSegments);

    public void RecordBackupAttempt(
        string providerId,
        string providerHost,
        string? segmentId,
        string priorFailures,
        bool pipelined = false)
    {
        var activity = _backupProviders.GetOrAdd(
            providerId,
            _ => new BackupProviderActivity(providerHost));
        var attempt = Interlocked.Increment(ref activity.Attempts);
        var stage = attempt == 1 ? "backup-needed" : "backup-attempt";
        var segment = ShortSegmentId(segmentId);

        if (attempt == 1)
            Log.Information(
                "playback-provider session={SessionId} request={RequestId} stage={Stage} " +
                "provider={Provider} segment={Segment} prior={PriorFailures} pipelined={Pipelined}",
                SessionId, RequestId, stage, providerHost, segment, priorFailures, pipelined);
        else
            Log.Debug(
                "playback-provider session={SessionId} request={RequestId} stage={Stage} " +
                "provider={Provider} segment={Segment} prior={PriorFailures} pipelined={Pipelined}",
                SessionId, RequestId, stage, providerHost, segment, priorFailures, pipelined);
    }

    public void RecordBackupOutcome(
        string providerId,
        string providerHost,
        string? segmentId,
        string outcome,
        long elapsedMs)
    {
        var activity = _backupProviders.GetOrAdd(
            providerId,
            _ => new BackupProviderActivity(providerHost));
        var firstRescue = false;
        switch (outcome)
        {
            case "rescued":
                firstRescue = Interlocked.Increment(ref activity.Rescued) == 1;
                break;
            case "missing":
                Interlocked.Increment(ref activity.Missing);
                break;
            case "timeout":
                Interlocked.Increment(ref activity.Timeouts);
                break;
            default:
                Interlocked.Increment(ref activity.Errors);
                break;
        }

        if (firstRescue)
            Log.Information(
                "playback-provider session={SessionId} request={RequestId} stage=backup-rescued " +
                "provider={Provider} segment={Segment} elapsedMs={ElapsedMs}",
                SessionId, RequestId, providerHost, ShortSegmentId(segmentId), elapsedMs);
        else
            Log.Debug(
                "playback-provider session={SessionId} request={RequestId} stage=backup-result " +
                "provider={Provider} segment={Segment} outcome={Outcome} elapsedMs={ElapsedMs}",
                SessionId, RequestId, providerHost, ShortSegmentId(segmentId), outcome, elapsedMs);
    }

    public void RecordFallbackRescue(
        string providerHost,
        string? segmentId,
        string priorFailures,
        long elapsedMs)
    {
        var rescue = Interlocked.Increment(ref _fallbackRescues);
        if (rescue == 1)
            Log.Information(
                "playback-provider session={SessionId} request={RequestId} stage=fallback-rescued " +
                "provider={Provider} segment={Segment} prior={PriorFailures} elapsedMs={ElapsedMs}",
                SessionId, RequestId, providerHost, ShortSegmentId(segmentId),
                priorFailures, elapsedMs);
        else
            Log.Debug(
                "playback-provider session={SessionId} request={RequestId} stage=fallback-rescued " +
                "provider={Provider} segment={Segment} prior={PriorFailures} elapsedMs={ElapsedMs}",
                SessionId, RequestId, providerHost, ShortSegmentId(segmentId),
                priorFailures, elapsedMs);
    }

    public void RecordProviderRotation(
        string failedProvider,
        string replacementProvider,
        bool replacementIsBackup,
        int unresolvedSegments,
        string reason)
    {
        Interlocked.Increment(ref _providerRotations);
        Log.Information(
            "playback-provider session={SessionId} request={RequestId} stage=pipeline-rotation " +
            "from={FailedProvider} to={ReplacementProvider} replacementIsBackup={ReplacementIsBackup} " +
            "unresolved={Unresolved} reason={Reason}",
            SessionId, RequestId, failedProvider, replacementProvider,
            replacementIsBackup, unresolvedSegments, reason);
    }

    public void RecordFallbackBudgetExhausted(int attemptedProviders, int remainingBackups)
    {
        Interlocked.Increment(ref _fallbackBudgetExhaustions);
        Log.Warning(
            "playback-provider session={SessionId} request={RequestId} stage=fallback-budget-exhausted " +
            "attemptedProviders={AttemptedProviders} remainingBackups={RemainingBackups}",
            SessionId, RequestId, attemptedProviders, remainingBackups);
    }

    public void RecordCacheHit() => Interlocked.Increment(ref _cacheHits);

    public void RecordCacheMiss() => Interlocked.Increment(ref _cacheMisses);

    public void RecordConnectionPermitWait(long elapsedMs, string priority, string outcome)
    {
        if (elapsedMs < _stallThreshold.TotalMilliseconds) return;
        Interlocked.Increment(ref _connectionPermitWaits);
        UpdateMaximum(ref _maxConnectionPermitWaitMs, elapsedMs);

        if (Interlocked.Exchange(ref _connectionPermitWaitLogged, 1) != 0) return;
        var buffer = BufferSnapshot();
        Log.Warning(
            "playback-session session={SessionId} request={RequestId} " +
            "stage=connection-permit-wait priority={Priority} outcome={Outcome} waitMs={WaitMs} " +
            "offset={Offset} bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            SessionId, RequestId, priority, outcome, elapsedMs, CurrentOffset,
            buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public void RecordProviderPoolWait(
        string provider,
        long elapsedMs,
        string outcome,
        int liveConnections,
        int activeConnections,
        int idleConnections,
        int pendingAcquisitions)
    {
        if (elapsedMs < _stallThreshold.TotalMilliseconds) return;
        Interlocked.Increment(ref _providerPoolWaits);
        UpdateMaximum(ref _maxProviderPoolWaitMs, elapsedMs);

        if (Interlocked.Exchange(ref _providerPoolWaitLogged, 1) != 0) return;
        var buffer = BufferSnapshot();
        Log.Warning(
            "playback-provider session={SessionId} request={RequestId} " +
            "stage=provider-pool-wait provider={Provider} outcome={Outcome} waitMs={WaitMs} " +
            "poolLive={PoolLive} poolActive={PoolActive} poolIdle={PoolIdle} " +
            "poolPending={PoolPending} offset={Offset} bufferedSegments={BufferedSegments} " +
            "inFlightSegments={InFlightSegments}",
            SessionId, RequestId, provider, outcome, elapsedMs,
            liveConnections, activeConnections, idleConnections, pendingAcquisitions,
            CurrentOffset, buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public PlaybackDiagnosticSnapshot Snapshot() => new(
        SessionId,
        RequestId,
        Interlocked.Read(ref _bytesServed),
        Interlocked.Read(ref _currentOffset),
        Math.Max(0, Volatile.Read(ref _bufferedSegments)),
        Math.Max(0, Volatile.Read(ref _inFlightSegments)),
        Volatile.Read(ref _upstreamStalls),
        Volatile.Read(ref _downstreamStalls),
        Interlocked.Read(ref _maxUpstreamStallMs),
        Interlocked.Read(ref _maxDownstreamStallMs),
        Volatile.Read(ref _fallbackRescues),
        Volatile.Read(ref _providerRotations),
        Volatile.Read(ref _fallbackBudgetExhaustions),
        Volatile.Read(ref _cacheHits),
        Volatile.Read(ref _cacheMisses),
        Volatile.Read(ref _connectionPermitWaits),
        Interlocked.Read(ref _maxConnectionPermitWaitMs),
        Volatile.Read(ref _providerPoolWaits),
        Interlocked.Read(ref _maxProviderPoolWaitMs),
        BackupSummary());

    public void Complete(
        string reason,
        string providerSummary,
        long bytesFetched,
        long failoverSaves,
        Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        var snapshot = Snapshot();
        const string message =
            "playback-session session={SessionId} request={RequestId} stage=request-end " +
            "reason={Reason} file={File} range={Range} durationMs={DurationMs} " +
            "firstByteMs={FirstByteMs} bytesServed={BytesServed} bytesFetched={BytesFetched} " +
            "offset={Offset} fileSize={FileSize} upstreamStalls={UpstreamStalls} " +
            "maxUpstreamStallMs={MaxUpstreamStallMs} downstreamStalls={DownstreamStalls} " +
            "maxDownstreamStallMs={MaxDownstreamStallMs} bufferedSegments={BufferedSegments} " +
            "inFlightSegments={InFlightSegments} failoverSaves={FailoverSaves} " +
            "fallbackRescues={FallbackRescues} providerRotations={ProviderRotations} " +
            "fallbackBudgetExhaustions={FallbackBudgetExhaustions} cacheHits={CacheHits} " +
            "cacheMisses={CacheMisses} connectionPermitWaits={ConnectionPermitWaits} " +
            "maxConnectionPermitWaitMs={MaxConnectionPermitWaitMs} " +
            "providerPoolWaits={ProviderPoolWaits} maxProviderPoolWaitMs={MaxProviderPoolWaitMs} " +
            "providers={Providers} " +
            "backups={Backups}";
        var firstByte = Interlocked.Read(ref _firstByteMs);

        if (exception is null)
            Log.Information(
                message,
                SessionId, RequestId, reason, _fileName, RequestedRange,
                _lifetime.ElapsedMilliseconds, firstByte < 0 ? "none" : firstByte,
                snapshot.BytesServed, bytesFetched, snapshot.CurrentOffset, _fileSize,
                snapshot.UpstreamStalls, snapshot.MaxUpstreamStallMs,
                snapshot.DownstreamStalls, snapshot.MaxDownstreamStallMs,
                snapshot.BufferedSegments, snapshot.InFlightSegments, failoverSaves,
                snapshot.FallbackRescues, snapshot.ProviderRotations,
                snapshot.FallbackBudgetExhaustions, snapshot.CacheHits, snapshot.CacheMisses,
                snapshot.ConnectionPermitWaits, snapshot.MaxConnectionPermitWaitMs,
                snapshot.ProviderPoolWaits, snapshot.MaxProviderPoolWaitMs,
                providerSummary, snapshot.BackupSummary);
        else
            Log.Warning(
                exception,
                message,
                SessionId, RequestId, reason, _fileName, RequestedRange,
                _lifetime.ElapsedMilliseconds, firstByte < 0 ? "none" : firstByte,
                snapshot.BytesServed, bytesFetched, snapshot.CurrentOffset, _fileSize,
                snapshot.UpstreamStalls, snapshot.MaxUpstreamStallMs,
                snapshot.DownstreamStalls, snapshot.MaxDownstreamStallMs,
                snapshot.BufferedSegments, snapshot.InFlightSegments, failoverSaves,
                snapshot.FallbackRescues, snapshot.ProviderRotations,
                snapshot.FallbackBudgetExhaustions, snapshot.CacheHits, snapshot.CacheMisses,
                snapshot.ConnectionPermitWaits, snapshot.MaxConnectionPermitWaitMs,
                snapshot.ProviderPoolWaits, snapshot.MaxProviderPoolWaitMs,
                providerSummary, snapshot.BackupSummary);
    }

    private void RecordStall(string kind, long elapsedMs, long offset)
    {
        bool shouldLog;
        lock (_stallLogLock)
        {
            var now = _lifetime.ElapsedMilliseconds;
            if (kind == "upstream-read")
            {
                _upstreamStalls++;
                _maxUpstreamStallMs = Math.Max(_maxUpstreamStallMs, elapsedMs);
                shouldLog = _upstreamStalls == 1 ||
                            now - _lastUpstreamStallLogMs >= StallLogInterval.TotalMilliseconds;
                if (shouldLog) _lastUpstreamStallLogMs = now;
            }
            else
            {
                _downstreamStalls++;
                _maxDownstreamStallMs = Math.Max(_maxDownstreamStallMs, elapsedMs);
                shouldLog = _downstreamStalls == 1 ||
                            now - _lastDownstreamStallLogMs >= StallLogInterval.TotalMilliseconds;
                if (shouldLog) _lastDownstreamStallLogMs = now;
            }
        }

        if (!shouldLog) return;
        var buffer = BufferSnapshot();
        Log.Warning(
            "playback-session session={SessionId} request={RequestId} stage=stall kind={Kind} " +
            "file={File} offset={Offset} waitMs={WaitMs} bufferedSegments={BufferedSegments} " +
            "inFlightSegments={InFlightSegments}",
            SessionId, RequestId, kind, _fileName, offset, elapsedMs,
            buffer.BufferedSegments, buffer.InFlightSegments);
    }

    private (int BufferedSegments, int InFlightSegments) BufferSnapshot() => (
        Math.Max(0, Volatile.Read(ref _bufferedSegments)),
        Math.Max(0, Volatile.Read(ref _inFlightSegments)));

    private string BackupSummary()
    {
        if (_backupProviders.IsEmpty) return "none";
        return string.Join(
            ';',
            _backupProviders
                .OrderBy(x => x.Value.Host, StringComparer.OrdinalIgnoreCase)
                .Select(x =>
                    $"{x.Value.Host}:attempts={Volatile.Read(ref x.Value.Attempts)}," +
                    $"rescued={Volatile.Read(ref x.Value.Rescued)}," +
                    $"missing={Volatile.Read(ref x.Value.Missing)}," +
                    $"timeouts={Volatile.Read(ref x.Value.Timeouts)}," +
                    $"errors={Volatile.Read(ref x.Value.Errors)}"));
    }

    private static string ShortSegmentId(string? segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId)) return "unknown";
        return segmentId.Length <= 160 ? segmentId : segmentId[..157] + "...";
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

    private static void UpdateMaximum(ref long target, long candidate)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (candidate <= current) return;
            if (Interlocked.CompareExchange(ref target, candidate, current) == current) return;
        }
    }

    private sealed class BackupProviderActivity(string host)
    {
        public string Host { get; } = host;
        public int Attempts;
        public int Rescued;
        public int Missing;
        public int Timeouts;
        public int Errors;
    }
}

internal sealed record PlaybackDiagnosticSnapshot(
    Guid SessionId,
    Guid RequestId,
    long BytesServed,
    long CurrentOffset,
    int BufferedSegments,
    int InFlightSegments,
    int UpstreamStalls,
    int DownstreamStalls,
    long MaxUpstreamStallMs,
    long MaxDownstreamStallMs,
    int FallbackRescues,
    int ProviderRotations,
    int FallbackBudgetExhaustions,
    int CacheHits,
    int CacheMisses,
    int ConnectionPermitWaits,
    long MaxConnectionPermitWaitMs,
    int ProviderPoolWaits,
    long MaxProviderPoolWaitMs,
    string BackupSummary);
