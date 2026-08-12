namespace NzbWebDAV.Services;

internal sealed class PlaybackRequestDiagnostics
{
    private static readonly TimeSpan DefaultStallThreshold = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _stallThreshold;
    private readonly PlaybackMetricsAccumulator _metrics = new();
    private readonly PlaybackReadAheadTracker _readAhead = new();
    private readonly PlaybackRequestLogger _logger;
    private long _bytesServed;
    private long _currentOffset;
    private long _maxOffset;
    private int _inFlightSegments;
    private int _bufferedSegments;
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
        RequestedRange = string.IsNullOrWhiteSpace(requestedRange) ? "full" : requestedRange;
        _currentOffset = Math.Max(0, initialOffset);
        _maxOffset = _currentOffset;
        _stallThreshold = stallThreshold ?? DefaultStallThreshold;
        _sessionStats = sessionStats;
        _logger = new PlaybackRequestLogger(
            SessionId,
            RequestId,
            Path,
            fileName,
            RequestedRange);
    }

    public Guid SessionId { get; }
    public Guid RequestId { get; }
    public string Path { get; }
    public string RequestedRange { get; }
    public long CurrentOffset => Interlocked.Read(ref _currentOffset);

    public void MarkStreamOpened(string fileName, long? fileSize, long effectiveStart)
    {
        Interlocked.Exchange(ref _currentOffset, Math.Max(0, effectiveStart));
        UpdateMaximum(ref _maxOffset, Math.Max(0, effectiveStart));
        _logger.StreamOpened(fileName, fileSize, effectiveStart);
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

        _logger.FirstByte(
            position - bytes,
            upstreamReadMs,
            downstreamWriteMs,
            BufferSnapshot());

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
            _sessionStats?.BeginWait(SessionId, isUpstream, elapsedMs);
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
        _logger.ZeroFill(segmentId, bytes, CurrentOffset, exception);
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
        _logger.BodyStallRecovery(
            count,
            providerId,
            providerHost,
            segmentId,
            transferredBytes,
            attempt,
            pipelined,
            BufferSnapshot());
    }

    public void UpstreamOperationStarted() =>
        Interlocked.Increment(ref _inFlightSegments);

    public void UpstreamOperationCompleted() =>
        DecrementNonNegative(ref _inFlightSegments);

    public void ReadAheadProducerStarted(long targetBytes) =>
        _readAhead.ProducerStarted(targetBytes);

    public void ReadAheadProducerCompleted(long targetBytes) =>
        _readAhead.ProducerCompleted(targetBytes);

    public void SegmentBuffered(long bytes = 0)
    {
        Interlocked.Increment(ref _bufferedSegments);
        _readAhead.SegmentBuffered(bytes);
    }

    public void SegmentDequeued(long bytes = 0)
    {
        DecrementNonNegative(ref _bufferedSegments);
        _readAhead.SegmentDequeued(bytes);
    }

    public void RecordBackupAttempt(
        string providerId,
        string providerHost,
        string? segmentId,
        string priorFailures,
        bool pipelined = false)
    {
        var attempt = _metrics.RecordBackupAttempt(providerId, providerHost);
        _logger.BackupAttempt(
            attempt,
            providerHost,
            segmentId,
            priorFailures,
            pipelined);
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
        _logger.BackupOutcome(
            firstRescue,
            providerHost,
            segmentId,
            outcome,
            elapsedMs);
    }

    public void RecordFallbackRescue(
        string providerHost,
        string? segmentId,
        string priorFailures,
        long elapsedMs)
    {
        var rescue = _metrics.RecordFallbackRescue();
        _logger.FallbackRescue(
            rescue,
            providerHost,
            segmentId,
            priorFailures,
            elapsedMs);
    }

    public void RecordProviderRotation(
        string failedProvider,
        string replacementProvider,
        bool replacementIsBackup,
        int unresolvedSegments,
        string reason)
    {
        _metrics.RecordProviderRotation();
        _logger.ProviderRotation(
            failedProvider,
            replacementProvider,
            replacementIsBackup,
            unresolvedSegments,
            reason);
    }

    public void RecordPipelineReset(
        string provider,
        int unresolvedSegments,
        string reason)
    {
        _logger.PipelineReset(provider, unresolvedSegments, reason);
    }

    public void RecordFallbackBudgetExhausted(int attemptedProviders, int remainingBackups)
    {
        _metrics.RecordFallbackBudgetExhaustion();
        _logger.FallbackBudgetExhausted(attemptedProviders, remainingBackups);
    }

    public void RecordCacheHit() => _metrics.RecordCacheHit();

    public void RecordCacheMiss() => _metrics.RecordCacheMiss();

    public void RecordConnectionPermitWait(long elapsedMs, string priority, string outcome)
    {
        if (elapsedMs < _stallThreshold.TotalMilliseconds) return;
        var wait = _metrics.RecordConnectionPermitWait(elapsedMs);
        _logger.ConnectionPermitWait(
            wait,
            elapsedMs,
            priority,
            outcome,
            CurrentOffset,
            BufferSnapshot());
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
        _logger.ProviderPoolWait(
            wait,
            provider,
            elapsedMs,
            outcome,
            liveConnections,
            activeConnections,
            idleConnections,
            pendingAcquisitions,
            CurrentOffset,
            BufferSnapshot());
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
        _logger.Complete(
            snapshot,
            reason,
            bytesFetched,
            failoverSaves,
            providerSummary,
            exception);
    }

    /// <summary>
    /// What this request contributes to its session's durable totals. Counters
    /// are request-lifetime cumulative and Complete() runs once, so summing the
    /// deltas of every request in a session double-counts nothing.
    /// </summary>
    private PlaybackRequestDelta BuildDelta(string reason, Exception? exception)
    {
        var metrics = _metrics.Snapshot();
        var readAhead = _readAhead.Complete();
        return new PlaybackRequestDelta(
            _startedAt,
            _logger.FirstByteMs,
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
            readAhead.ByteMilliseconds,
            readAhead.MeasuredMilliseconds,
            readAhead.MinimumBytes,
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

        // Reported immediately, not at request end: a sequential stream is one
        // long request, and a live view must not read zero while it is stuck.
        _sessionStats?.RecordWait(SessionId, isUpstream, delta, elapsedMs, isNewWait, headOfLine);
        _logger.Stall(
            isUpstream,
            headOfLine,
            wait,
            elapsedMs,
            offset,
            outcome,
            onset ?? BufferSnapshot());
    }

    private (int BufferedSegments, int InFlightSegments) BufferSnapshot() => (
        Math.Max(0, Volatile.Read(ref _bufferedSegments)),
        Math.Max(0, Volatile.Read(ref _inFlightSegments)));

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
