using System.Diagnostics;
using Serilog;
using Serilog.Events;

namespace NzbWebDAV.Services;

internal sealed class PlaybackRequestDiagnostics
{
    private static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StallLogInterval = TimeSpan.FromSeconds(10);
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private readonly TimeSpan _stallThreshold;
    private readonly object _stallLogLock = new();
    private readonly PlaybackMetricsAccumulator _metrics = new();
    private string _fileName;
    private long? _fileSize;
    private long _streamOpenMs = -1;
    private long _firstByteMs = -1;
    private long _bytesServed;
    private long _currentOffset;
    private long _maxOffset;
    private int _inFlightSegments;
    private int _bufferedSegments;
    private long _lastUpstreamStallLogMs = long.MinValue;
    private long _lastDownstreamStallLogMs = long.MinValue;
    private int _connectionPermitWaitInfoLogged;
    private int _connectionPermitWaitWarningLogged;
    private int _providerPoolWaitInfoLogged;
    private int _providerPoolWaitWarningLogged;
    private int _completed;

    private readonly PlaybackSessionStats? _sessionStats;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public PlaybackRequestDiagnostics(
        Guid sessionId,
        string path,
        string fileName,
        string? requestedRange,
        long initialOffset = 0,
        TimeSpan? stallThreshold = null,
        PlaybackSessionStats? sessionStats = null)
    {
        SessionId = sessionId;
        RequestId = Guid.NewGuid();
        Path = path;
        _fileName = fileName;
        RequestedRange = string.IsNullOrWhiteSpace(requestedRange) ? "full" : requestedRange;
        _currentOffset = Math.Max(0, initialOffset);
        _maxOffset = _currentOffset;
        _stallThreshold = stallThreshold ?? DefaultStallThreshold;
        _sessionStats = sessionStats;

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
        UpdateMaximum(ref _maxOffset, Math.Max(0, effectiveStart));
        Interlocked.CompareExchange(ref _streamOpenMs, _lifetime.ElapsedMilliseconds, -1);
        Log.Debug(
            "playback-session session={SessionId} request={RequestId} stage=stream-open " +
            "file={File} size={FileSize} offset={Offset} openMs={OpenMs}",
            SessionId, RequestId, _fileName, _fileSize, effectiveStart, _streamOpenMs);
    }

    /// <summary>
    /// Buffer depth as it was when a blocking transfer step began. Counters read
    /// after the fact describe the recovery, not the stall: the producer refills
    /// while the consumer is stuck, so a full buffer at log time says nothing
    /// about how full it was when the wait started.
    /// </summary>
    public (int BufferedSegments, int InFlightSegments) CaptureBufferState() => BufferSnapshot();

    public void RecordTransfer(
        int bytes,
        long position,
        long upstreamReadMs,
        long downstreamWriteMs,
        (int BufferedSegments, int InFlightSegments)? upstreamOnset = null,
        (int BufferedSegments, int InFlightSegments)? downstreamOnset = null,
        long upstreamReportedMs = 0,
        long downstreamReportedMs = 0)
    {
        if (bytes <= 0) return;
        Interlocked.Add(ref _bytesServed, bytes);
        Interlocked.Exchange(ref _currentOffset, position);
        UpdateMaximum(ref _maxOffset, position);

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
            RecordWait("upstream-read", upstreamReadMs, upstreamReportedMs, position - bytes, upstreamOnset);
        if (downstreamWriteMs >= _stallThreshold.TotalMilliseconds)
            RecordWait("downstream-write", downstreamWriteMs, downstreamReportedMs, position - bytes,
                downstreamOnset);
    }

    /// <summary>
    /// Reports a wait that is still going on, so the live view does not read
    /// zero while a viewer sits buffering. Returns the elapsed time now
    /// accounted for; pass it back as <c>reportedMs</c> next time and to
    /// <see cref="RecordTransfer"/> so the same milliseconds are counted once.
    /// </summary>
    public long ReportWaitProgress(
        bool isUpstream,
        long elapsedMs,
        long reportedMs,
        long offset,
        (int BufferedSegments, int InFlightSegments)? onset = null)
    {
        if (elapsedMs < _stallThreshold.TotalMilliseconds) return reportedMs;
        if (reportedMs <= 0)
            _sessionStats?.BeginWait(SessionId, isUpstream);
        RecordWait(isUpstream ? "upstream-read" : "downstream-write", elapsedMs, reportedMs, offset, onset);
        return elapsedMs;
    }

    /// <summary>
    /// A wait that ended without delivering bytes — the client went away, or the
    /// source reached EOF or faulted. Counting these is what keeps a stall that
    /// ends in a player abort from being reported as a flawless request.
    /// </summary>
    public void RecordAbandonedWait(
        bool isUpstream,
        long elapsedMs,
        long reportedMs,
        long offset,
        string outcome,
        (int BufferedSegments, int InFlightSegments)? onset = null)
    {
        if (elapsedMs < _stallThreshold.TotalMilliseconds) return;
        RecordWait(
            isUpstream ? "upstream-read" : "downstream-write",
            elapsedMs, reportedMs, offset, onset, outcome);
    }

    public void EndWait(bool isUpstream, long reportedMs)
    {
        if (reportedMs > 0)
            _sessionStats?.EndWait(SessionId, isUpstream);
    }

    /// <summary>
    /// Bytes that were never retrieved and were served as zeros to keep the
    /// stream alive. The player saw corrupt content: this is the one counter on
    /// the request that means the data itself was wrong, not merely late.
    /// </summary>
    public void RecordZeroFill(
        string segmentId,
        long bytes,
        Exception? exception = null)
    {
        _metrics.RecordZeroFill(bytes);
        _sessionStats?.RecordZeroFill(SessionId, bytes);
        const string message =
            "playback-session session={SessionId} request={RequestId} stage=zero-fill " +
            "file={File} segment={Segment} bytes={Bytes} offset={Offset} cause={Cause}";
        if (exception is null)
            Log.Warning(
                message,
                SessionId, RequestId, _fileName, ShortSegmentId(segmentId), bytes,
                CurrentOffset, "missing");
        else
            Log.Warning(
                exception,
                message,
                SessionId, RequestId, _fileName, ShortSegmentId(segmentId), bytes,
                CurrentOffset, exception.GetType().Name);
    }

    /// <summary>
    /// A body that stopped delivering mid-transfer and was refetched. Logged at
    /// Warning on first occurrence: the stream recovered, but a wedged provider
    /// socket is exactly the kind of fault that is invisible once it heals.
    /// </summary>
    public void RecordBodyStallRecovery(
        string? providerId,
        string? providerHost,
        string segmentId,
        long transferredBytes,
        int attempt,
        bool pipelined = false)
    {
        var count = _metrics.RecordBodyStallRecovery();
        _sessionStats?.RecordBodyStallRecovery(SessionId);
        var buffer = BufferSnapshot();
        Log.Write(
            count == 1 ? LogEventLevel.Warning : LogEventLevel.Debug,
            "playback-session session={SessionId} request={RequestId} stage=body-stall-recovery " +
            "file={File} providerId={ProviderId} provider={Provider} segment={Segment} " +
            "transferredBytes={TransferredBytes} attempt={Attempt} pipelined={Pipelined} " +
            "bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            SessionId, RequestId, _fileName,
            string.IsNullOrWhiteSpace(providerId) ? "unknown" : providerId,
            string.IsNullOrWhiteSpace(providerHost) ? "unknown" : providerHost,
            ShortSegmentId(segmentId), transferredBytes, attempt, pipelined,
            buffer.BufferedSegments, buffer.InFlightSegments);
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
        var attempt = _metrics.RecordBackupAttempt(providerId, providerHost);
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
        var firstRescue = _metrics.RecordBackupOutcome(
            providerId,
            providerHost,
            outcome);

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
        var rescue = _metrics.RecordFallbackRescue();
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
        _metrics.RecordProviderRotation();
        Log.Information(
            "playback-provider session={SessionId} request={RequestId} stage=pipeline-rotation " +
            "from={FailedProvider} to={ReplacementProvider} replacementIsBackup={ReplacementIsBackup} " +
            "unresolved={Unresolved} reason={Reason}",
            SessionId, RequestId, failedProvider, replacementProvider,
            replacementIsBackup, unresolvedSegments, reason);
    }

    public void RecordPipelineReset(
        string provider,
        int unresolvedSegments,
        string reason)
    {
        Log.Information(
            "playback-provider session={SessionId} request={RequestId} stage=pipeline-reset " +
            "provider={Provider} unresolved={Unresolved} reason={Reason}",
            SessionId, RequestId, provider, unresolvedSegments, reason);
    }

    public void RecordFallbackBudgetExhausted(int attemptedProviders, int remainingBackups)
    {
        _metrics.RecordFallbackBudgetExhaustion();
        Log.Warning(
            "playback-provider session={SessionId} request={RequestId} stage=fallback-budget-exhausted " +
            "attemptedProviders={AttemptedProviders} remainingBackups={RemainingBackups}",
            SessionId, RequestId, attemptedProviders, remainingBackups);
    }

    public void RecordCacheHit() => _metrics.RecordCacheHit();

    public void RecordCacheMiss() => _metrics.RecordCacheMiss();

    public void RecordConnectionPermitWait(long elapsedMs, string priority, string outcome)
    {
        if (elapsedMs < _stallThreshold.TotalMilliseconds) return;
        var wait = _metrics.RecordConnectionPermitWait(elapsedMs);
        var warning = PlaybackIssueThresholds.WaitsMatter(wait.Count, wait.MaxMs);
        ref var logged = ref (warning
            ? ref _connectionPermitWaitWarningLogged
            : ref _connectionPermitWaitInfoLogged);
        if (Interlocked.Exchange(ref logged, 1) != 0) return;
        var buffer = BufferSnapshot();
        Log.Write(
            warning ? LogEventLevel.Warning : LogEventLevel.Information,
            "playback-session session={SessionId} request={RequestId} " +
            "stage=connection-permit-wait priority={Priority} outcome={Outcome} waitMs={WaitMs} waits={Waits} " +
            "offset={Offset} bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            SessionId, RequestId, priority, outcome, elapsedMs, wait.Count, CurrentOffset,
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
        var wait = _metrics.RecordProviderPoolWait(elapsedMs);
        var warning = PlaybackIssueThresholds.WaitsMatter(wait.Count, wait.MaxMs);
        ref var logged = ref (warning
            ? ref _providerPoolWaitWarningLogged
            : ref _providerPoolWaitInfoLogged);
        if (Interlocked.Exchange(ref logged, 1) != 0) return;
        var buffer = BufferSnapshot();
        Log.Write(
            warning ? LogEventLevel.Warning : LogEventLevel.Information,
            "playback-provider session={SessionId} request={RequestId} " +
            "stage=provider-pool-wait provider={Provider} outcome={Outcome} waitMs={WaitMs} waits={Waits} " +
            "poolLive={PoolLive} poolActive={PoolActive} poolIdle={PoolIdle} " +
            "poolPending={PoolPending} offset={Offset} bufferedSegments={BufferedSegments} " +
            "inFlightSegments={InFlightSegments}",
            SessionId, RequestId, provider, outcome, elapsedMs, wait.Count,
            liveConnections, activeConnections, idleConnections, pendingAcquisitions,
            CurrentOffset, buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public PlaybackDiagnosticSnapshot Snapshot()
    {
        var metrics = _metrics.Snapshot();
        return new PlaybackDiagnosticSnapshot(
            SessionId,
            RequestId,
            Interlocked.Read(ref _bytesServed),
            Interlocked.Read(ref _currentOffset),
            Math.Max(0, Volatile.Read(ref _bufferedSegments)),
            Math.Max(0, Volatile.Read(ref _inFlightSegments)),
            metrics.UpstreamStalls,
            metrics.DownstreamStalls,
            metrics.MaxUpstreamStallMs,
            metrics.MaxDownstreamStallMs,
            metrics.TotalUpstreamStallMs,
            metrics.TotalDownstreamStallMs,
            metrics.HeadOfLineStalls,
            metrics.TotalHeadOfLineStallMs,
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
            metrics.BackupSummary);
    }

    public void Complete(
        string reason,
        string providerSummary,
        long bytesFetched,
        long failoverSaves,
        Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        var snapshot = Snapshot();
        _sessionStats?.Fold(SessionId, BuildDelta(reason, exception));
        const string message =
            "playback-session session={SessionId} request={RequestId} stage=request-end " +
            "reason={Reason} file={File} range={Range} durationMs={DurationMs} " +
            "firstByteMs={FirstByteMs} bytesServed={BytesServed} bytesFetched={BytesFetched} " +
            "offset={Offset} fileSize={FileSize} upstreamStalls={UpstreamStalls} " +
            "maxUpstreamStallMs={MaxUpstreamStallMs} totalUpstreamStallMs={TotalUpstreamStallMs} " +
            // Order must match the argument list below exactly: Serilog binds
            // these positionally, so a placeholder inserted out of order prints
            // one counter's value under another counter's name.
            "downstreamStalls={DownstreamStalls} maxDownstreamStallMs={MaxDownstreamStallMs} " +
            "totalDownstreamStallMs={TotalDownstreamStallMs} " +
            "headOfLineStalls={HeadOfLineStalls} totalHeadOfLineStallMs={TotalHeadOfLineStallMs} " +
            "bufferedSegments={BufferedSegments} " +
            "inFlightSegments={InFlightSegments} failoverSaves={FailoverSaves} " +
            "fallbackRescues={FallbackRescues} providerRotations={ProviderRotations} " +
            "fallbackBudgetExhaustions={FallbackBudgetExhaustions} cacheHits={CacheHits} " +
            "cacheMisses={CacheMisses} connectionPermitWaits={ConnectionPermitWaits} " +
            "maxConnectionPermitWaitMs={MaxConnectionPermitWaitMs} " +
            "providerPoolWaits={ProviderPoolWaits} maxProviderPoolWaitMs={MaxProviderPoolWaitMs} " +
            "zeroFilledSegments={ZeroFilledSegments} zeroFilledBytes={ZeroFilledBytes} " +
            "bodyStallRecoveries={BodyStallRecoveries} " +
            "providers={Providers} " +
            "backups={Backups}";
        var firstByte = Interlocked.Read(ref _firstByteMs);
        var level = PlaybackOutcomeClassifier.CompletionLogLevel(
            snapshot,
            reason,
            exception);

        if (exception is null)
            Log.Write(
                level,
                message,
                SessionId, RequestId, reason, _fileName, RequestedRange,
                _lifetime.ElapsedMilliseconds, firstByte < 0 ? "none" : firstByte,
                snapshot.BytesServed, bytesFetched, snapshot.CurrentOffset, _fileSize,
                snapshot.UpstreamStalls, snapshot.MaxUpstreamStallMs,
                snapshot.TotalUpstreamStallMs, snapshot.DownstreamStalls,
                snapshot.MaxDownstreamStallMs, snapshot.TotalDownstreamStallMs,
                snapshot.HeadOfLineStalls, snapshot.TotalHeadOfLineStallMs,
                snapshot.BufferedSegments, snapshot.InFlightSegments, failoverSaves,
                snapshot.FallbackRescues, snapshot.ProviderRotations,
                snapshot.FallbackBudgetExhaustions, snapshot.CacheHits, snapshot.CacheMisses,
                snapshot.ConnectionPermitWaits, snapshot.MaxConnectionPermitWaitMs,
                snapshot.ProviderPoolWaits, snapshot.MaxProviderPoolWaitMs,
                snapshot.ZeroFilledSegments, snapshot.ZeroFilledBytes,
                snapshot.BodyStallRecoveries,
                providerSummary, snapshot.BackupSummary);
        else
            Log.Write(
                level,
                exception,
                message,
                SessionId, RequestId, reason, _fileName, RequestedRange,
                _lifetime.ElapsedMilliseconds, firstByte < 0 ? "none" : firstByte,
                snapshot.BytesServed, bytesFetched, snapshot.CurrentOffset, _fileSize,
                snapshot.UpstreamStalls, snapshot.MaxUpstreamStallMs,
                snapshot.TotalUpstreamStallMs, snapshot.DownstreamStalls,
                snapshot.MaxDownstreamStallMs, snapshot.TotalDownstreamStallMs,
                snapshot.HeadOfLineStalls, snapshot.TotalHeadOfLineStallMs,
                snapshot.BufferedSegments, snapshot.InFlightSegments, failoverSaves,
                snapshot.FallbackRescues, snapshot.ProviderRotations,
                snapshot.FallbackBudgetExhaustions, snapshot.CacheHits, snapshot.CacheMisses,
                snapshot.ConnectionPermitWaits, snapshot.MaxConnectionPermitWaitMs,
                snapshot.ProviderPoolWaits, snapshot.MaxProviderPoolWaitMs,
                snapshot.ZeroFilledSegments, snapshot.ZeroFilledBytes,
                snapshot.BodyStallRecoveries,
                providerSummary, snapshot.BackupSummary);
    }

    /// <summary>
    /// What this request contributes to its session's durable totals. Counters
    /// are request-lifetime cumulative and Complete() runs once, so summing the
    /// deltas of every request in a session double-counts nothing.
    /// </summary>
    private PlaybackRequestDelta BuildDelta(string reason, Exception? exception)
    {
        var firstByte = Interlocked.Read(ref _firstByteMs);
        var metrics = _metrics.Snapshot();
        return new PlaybackRequestDelta(
            _startedAt,
            firstByte < 0 ? null : firstByte,
            Interlocked.Read(ref _maxOffset),
            metrics.FallbackRescues,
            metrics.ProviderRotations,
            metrics.FallbackBudgetExhaustions,
            metrics.CacheHits,
            metrics.CacheMisses,
            metrics.ConnectionPermitWaits,
            metrics.MaxConnectionPermitWaitMs,
            metrics.ProviderPoolWaits,
            metrics.MaxProviderPoolWaitMs,
            // Integrity counters are reported live to PlaybackSessionStats, just
            // like stalls. Sending them again in the completion delta would
            // double-count every recovered or substituted segment.
            0,
            0,
            0,
            metrics.BackupProviders,
            exception is null ? null : Truncate($"{reason}: {exception.Message}", 500));
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>
    /// Accounts for one wait. <paramref name="reportedMs"/> is how much of this
    /// same wait has already been counted while it was still running, so a wait
    /// reported live and then again on completion contributes its duration once
    /// and is counted as one wait, not several.
    /// </summary>
    private void RecordWait(
        string kind,
        long elapsedMs,
        long reportedMs,
        long offset,
        (int BufferedSegments, int InFlightSegments)? onset = null,
        string outcome = "served")
    {
        bool shouldLog;
        var isUpstream = kind == "upstream-read";
        var isNewWait = reportedMs <= 0;
        var delta = Math.Max(0, elapsedMs - Math.Max(0, reportedMs));
        // Two different faults look identical in a stall count. If nothing was
        // ready when the wait began, the source could not keep up. If segments
        // were already downloaded and the reader waited anyway, they were stuck
        // behind the one article at the head of the queue — plenty of data on
        // hand, all of it unusable. The fixes are opposites (fetch harder vs.
        // stop one slow article blocking the rest), so the counters must part.
        var headOfLine = isUpstream && onset is { BufferedSegments: > 0 };
        var wait = _metrics.RecordWait(
            isUpstream,
            delta,
            elapsedMs,
            isNewWait,
            headOfLine);
        lock (_stallLogLock)
        {
            var now = _lifetime.ElapsedMilliseconds;
            if (isUpstream)
            {
                shouldLog = _lastUpstreamStallLogMs == long.MinValue ||
                            now - _lastUpstreamStallLogMs >= StallLogInterval.TotalMilliseconds;
                if (shouldLog) _lastUpstreamStallLogMs = now;
            }
            else
            {
                shouldLog = _lastDownstreamStallLogMs == long.MinValue ||
                            now - _lastDownstreamStallLogMs >= StallLogInterval.TotalMilliseconds;
                if (shouldLog) _lastDownstreamStallLogMs = now;
            }
        }

        // Reported immediately, not at request end: a sequential stream is one
        // long request, and a live view must not read zero while it is stuck.
        _sessionStats?.RecordWait(SessionId, isUpstream, delta, elapsedMs, isNewWait, headOfLine);

        if (!shouldLog) return;
        var buffer = onset ?? BufferSnapshot();
        // A slow write is the client pacing itself, not a fault — a player that
        // races ahead and then throttles produces these on flawless playback, so
        // they must not warn. They still log at Information rather than Debug:
        // this is the evidence that clears the server, and an operator reading
        // default-level logs needs to see it next to the upstream stall instead
        // of concluding the source was at fault.
        //
        // Upstream waits warn only once they are big enough for the page to call
        // a source issue. A lone 1 s wait behind a full buffer reached nobody,
        // and warning about it drowns the waits that did.
        var level = isUpstream && PlaybackIssueThresholds.StallsMatter(wait.Count, wait.MaxMs)
            ? LogEventLevel.Warning
            : LogEventLevel.Information;
        Log.Write(
            level,
            "playback-session session={SessionId} request={RequestId} stage=stall kind={Kind} " +
            "file={File} offset={Offset} waitMs={WaitMs} outcome={Outcome} waits={Waits} " +
            "blocked={Blocked} bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            SessionId, RequestId, kind, _fileName, offset, elapsedMs, outcome, wait.Count,
            isUpstream ? (headOfLine ? "head-of-line" : "source") : "client",
            buffer.BufferedSegments, buffer.InFlightSegments);
    }

    private (int BufferedSegments, int InFlightSegments) BufferSnapshot() => (
        Math.Max(0, Volatile.Read(ref _bufferedSegments)),
        Math.Max(0, Volatile.Read(ref _inFlightSegments)));

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
}
