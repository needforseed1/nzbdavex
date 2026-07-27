using System.Diagnostics;
using Serilog;
using Serilog.Events;

namespace NzbWebDAV.Services;

/// <summary>
/// Owns the structured log contract for one playback request. Metrics and
/// lifecycle decisions stay in <see cref="PlaybackRequestDiagnostics"/>; this
/// class is responsible only for presentation, throttling, and log levels.
/// </summary>
internal sealed class PlaybackRequestLogger
{
    private static readonly TimeSpan StallLogInterval = TimeSpan.FromSeconds(10);
    private readonly Stopwatch _lifetime = Stopwatch.StartNew();
    private readonly object _stallLogLock = new();
    private readonly Guid _sessionId;
    private readonly Guid _requestId;
    private readonly string _requestedRange;
    private string _fileName;
    private long? _fileSize;
    private long _streamOpenMs = -1;
    private long _firstByteMs = -1;
    private long _lastUpstreamStallLogMs = long.MinValue;
    private long _lastDownstreamStallLogMs = long.MinValue;
    private int _connectionPermitWaitInfoLogged;
    private int _connectionPermitWaitWarningLogged;
    private int _providerPoolWaitInfoLogged;
    private int _providerPoolWaitWarningLogged;

    public PlaybackRequestLogger(
        Guid sessionId,
        Guid requestId,
        string path,
        string fileName,
        string requestedRange)
    {
        _sessionId = sessionId;
        _requestId = requestId;
        _fileName = fileName;
        _requestedRange = requestedRange;

        Log.Information(
            "playback-session session={SessionId} request={RequestId} stage=request-start " +
            "file={File} path={Path} range={Range}",
            _sessionId, _requestId, _fileName, path, _requestedRange);
    }

    public long? FirstByteMs
    {
        get
        {
            var firstByte = Interlocked.Read(ref _firstByteMs);
            return firstByte < 0 ? null : firstByte;
        }
    }

    public void StreamOpened(string fileName, long? fileSize, long effectiveStart)
    {
        if (!string.IsNullOrWhiteSpace(fileName)) _fileName = fileName;
        _fileSize = fileSize;
        Interlocked.CompareExchange(
            ref _streamOpenMs,
            _lifetime.ElapsedMilliseconds,
            -1);
        Log.Debug(
            "playback-session session={SessionId} request={RequestId} stage=stream-open " +
            "file={File} size={FileSize} offset={Offset} openMs={OpenMs}",
            _sessionId, _requestId, _fileName, _fileSize, effectiveStart, _streamOpenMs);
    }

    public void FirstByte(
        long offset,
        long upstreamReadMs,
        long downstreamWriteMs,
        (int BufferedSegments, int InFlightSegments) buffer)
    {
        if (Interlocked.CompareExchange(
                ref _firstByteMs,
                _lifetime.ElapsedMilliseconds,
                -1) != -1)
            return;

        Log.Information(
            "playback-session session={SessionId} request={RequestId} stage=first-byte " +
            "file={File} offset={Offset} firstByteMs={FirstByteMs} openMs={OpenMs} " +
            "upstreamReadMs={UpstreamReadMs} downstreamWriteMs={DownstreamWriteMs} " +
            "bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            _sessionId, _requestId, _fileName, offset, _firstByteMs,
            Interlocked.Read(ref _streamOpenMs), upstreamReadMs, downstreamWriteMs,
            buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public void ZeroFill(
        string segmentId,
        long bytes,
        long offset,
        Exception? exception)
    {
        const string message =
            "playback-session session={SessionId} request={RequestId} stage=zero-fill " +
            "file={File} segment={Segment} bytes={Bytes} offset={Offset} cause={Cause}";
        if (exception is null)
            Log.Warning(
                message,
                _sessionId, _requestId, _fileName, ShortSegmentId(segmentId), bytes,
                offset, "missing");
        else
            Log.Warning(
                exception,
                message,
                _sessionId, _requestId, _fileName, ShortSegmentId(segmentId), bytes,
                offset, exception.GetType().Name);
    }

    public void BodyStallRecovery(
        int recoveryCount,
        string? providerId,
        string? providerHost,
        string segmentId,
        long transferredBytes,
        int attempt,
        bool pipelined,
        (int BufferedSegments, int InFlightSegments) buffer)
    {
        Log.Write(
            recoveryCount == 1 ? LogEventLevel.Warning : LogEventLevel.Debug,
            "playback-session session={SessionId} request={RequestId} stage=body-stall-recovery " +
            "file={File} providerId={ProviderId} provider={Provider} segment={Segment} " +
            "transferredBytes={TransferredBytes} attempt={Attempt} pipelined={Pipelined} " +
            "bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            _sessionId, _requestId, _fileName,
            string.IsNullOrWhiteSpace(providerId) ? "unknown" : providerId,
            string.IsNullOrWhiteSpace(providerHost) ? "unknown" : providerHost,
            ShortSegmentId(segmentId), transferredBytes, attempt, pipelined,
            buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public void BackupAttempt(
        long attempt,
        string providerHost,
        string? segmentId,
        string priorFailures,
        bool pipelined)
    {
        var stage = attempt == 1 ? "backup-needed" : "backup-attempt";
        var segment = ShortSegmentId(segmentId);

        if (attempt == 1)
            Log.Information(
                "playback-provider session={SessionId} request={RequestId} stage={Stage} " +
                "provider={Provider} segment={Segment} prior={PriorFailures} pipelined={Pipelined}",
                _sessionId, _requestId, stage, providerHost, segment, priorFailures, pipelined);
        else
            Log.Debug(
                "playback-provider session={SessionId} request={RequestId} stage={Stage} " +
                "provider={Provider} segment={Segment} prior={PriorFailures} pipelined={Pipelined}",
                _sessionId, _requestId, stage, providerHost, segment, priorFailures, pipelined);
    }

    public void BackupOutcome(
        bool firstRescue,
        string providerHost,
        string? segmentId,
        string outcome,
        long elapsedMs)
    {
        if (firstRescue)
            Log.Information(
                "playback-provider session={SessionId} request={RequestId} stage=backup-rescued " +
                "provider={Provider} segment={Segment} elapsedMs={ElapsedMs}",
                _sessionId, _requestId, providerHost, ShortSegmentId(segmentId), elapsedMs);
        else
            Log.Debug(
                "playback-provider session={SessionId} request={RequestId} stage=backup-result " +
                "provider={Provider} segment={Segment} outcome={Outcome} elapsedMs={ElapsedMs}",
                _sessionId, _requestId, providerHost, ShortSegmentId(segmentId), outcome, elapsedMs);
    }

    public void FallbackRescue(
        int rescueCount,
        string providerHost,
        string? segmentId,
        string priorFailures,
        long elapsedMs)
    {
        var level = rescueCount == 1
            ? LogEventLevel.Information
            : LogEventLevel.Debug;
        Log.Write(
            level,
            "playback-provider session={SessionId} request={RequestId} stage=fallback-rescued " +
            "provider={Provider} segment={Segment} prior={PriorFailures} elapsedMs={ElapsedMs}",
            _sessionId, _requestId, providerHost, ShortSegmentId(segmentId),
            priorFailures, elapsedMs);
    }

    public void ProviderRotation(
        string failedProvider,
        string replacementProvider,
        bool replacementIsBackup,
        int unresolvedSegments,
        string reason)
    {
        Log.Information(
            "playback-provider session={SessionId} request={RequestId} stage=pipeline-rotation " +
            "from={FailedProvider} to={ReplacementProvider} replacementIsBackup={ReplacementIsBackup} " +
            "unresolved={Unresolved} reason={Reason}",
            _sessionId, _requestId, failedProvider, replacementProvider,
            replacementIsBackup, unresolvedSegments, reason);
    }

    public void PipelineReset(
        string provider,
        int unresolvedSegments,
        string reason)
    {
        Log.Information(
            "playback-provider session={SessionId} request={RequestId} stage=pipeline-reset " +
            "provider={Provider} unresolved={Unresolved} reason={Reason}",
            _sessionId, _requestId, provider, unresolvedSegments, reason);
    }

    public void FallbackBudgetExhausted(
        int attemptedProviders,
        int remainingBackups)
    {
        Log.Warning(
            "playback-provider session={SessionId} request={RequestId} stage=fallback-budget-exhausted " +
            "attemptedProviders={AttemptedProviders} remainingBackups={RemainingBackups}",
            _sessionId, _requestId, attemptedProviders, remainingBackups);
    }

    public void ConnectionPermitWait(
        PlaybackWaitUpdate wait,
        long elapsedMs,
        string priority,
        string outcome,
        long offset,
        (int BufferedSegments, int InFlightSegments) buffer)
    {
        var warning = PlaybackIssueThresholds.WaitsMatter(wait.Count, wait.MaxMs);
        ref var logged = ref (warning
            ? ref _connectionPermitWaitWarningLogged
            : ref _connectionPermitWaitInfoLogged);
        if (Interlocked.Exchange(ref logged, 1) != 0) return;

        Log.Write(
            warning ? LogEventLevel.Warning : LogEventLevel.Information,
            "playback-session session={SessionId} request={RequestId} " +
            "stage=connection-permit-wait priority={Priority} outcome={Outcome} waitMs={WaitMs} waits={Waits} " +
            "offset={Offset} bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            _sessionId, _requestId, priority, outcome, elapsedMs, wait.Count, offset,
            buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public void ProviderPoolWait(
        PlaybackWaitUpdate wait,
        string provider,
        long elapsedMs,
        string outcome,
        int liveConnections,
        int activeConnections,
        int idleConnections,
        int pendingAcquisitions,
        long offset,
        (int BufferedSegments, int InFlightSegments) buffer)
    {
        var warning = PlaybackIssueThresholds.WaitsMatter(wait.Count, wait.MaxMs);
        ref var logged = ref (warning
            ? ref _providerPoolWaitWarningLogged
            : ref _providerPoolWaitInfoLogged);
        if (Interlocked.Exchange(ref logged, 1) != 0) return;

        Log.Write(
            warning ? LogEventLevel.Warning : LogEventLevel.Information,
            "playback-provider session={SessionId} request={RequestId} " +
            "stage=provider-pool-wait provider={Provider} outcome={Outcome} waitMs={WaitMs} waits={Waits} " +
            "poolLive={PoolLive} poolActive={PoolActive} poolIdle={PoolIdle} " +
            "poolPending={PoolPending} offset={Offset} bufferedSegments={BufferedSegments} " +
            "inFlightSegments={InFlightSegments}",
            _sessionId, _requestId, provider, outcome, elapsedMs, wait.Count,
            liveConnections, activeConnections, idleConnections, pendingAcquisitions,
            offset, buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public void Stall(
        bool isUpstream,
        bool headOfLine,
        PlaybackWaitUpdate wait,
        long elapsedMs,
        long offset,
        string outcome,
        (int BufferedSegments, int InFlightSegments) buffer)
    {
        if (!ShouldLogStall(isUpstream)) return;

        // A slow write is client pacing, not a server fault. Upstream waits warn
        // only once they meet the same threshold used by the playback page.
        var level = isUpstream && PlaybackIssueThresholds.StallsMatter(wait.Count, wait.MaxMs)
            ? LogEventLevel.Warning
            : LogEventLevel.Information;
        Log.Write(
            level,
            "playback-session session={SessionId} request={RequestId} stage=stall kind={Kind} " +
            "file={File} offset={Offset} waitMs={WaitMs} outcome={Outcome} waits={Waits} " +
            "blocked={Blocked} bufferedSegments={BufferedSegments} inFlightSegments={InFlightSegments}",
            _sessionId, _requestId, isUpstream ? "upstream-read" : "downstream-write",
            _fileName, offset, elapsedMs, outcome, wait.Count,
            isUpstream ? (headOfLine ? "head-of-line" : "source") : "client",
            buffer.BufferedSegments, buffer.InFlightSegments);
    }

    public void Complete(
        PlaybackDiagnosticSnapshot snapshot,
        string reason,
        long bytesFetched,
        long failoverSaves,
        string providerSummary,
        Exception? exception)
    {
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
        var firstByte = FirstByteMs;
        var level = PlaybackOutcomeClassifier.CompletionLogLevel(
            snapshot,
            reason,
            exception);
        var arguments = new object?[]
        {
            _sessionId, _requestId, reason, _fileName, _requestedRange,
            _lifetime.ElapsedMilliseconds,
            firstByte.HasValue ? (object)firstByte.Value : "none",
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
            providerSummary, snapshot.BackupSummary,
        };

        if (exception is null)
            Log.Write(level, message, arguments);
        else
            Log.Write(level, exception, message, arguments);
    }

    private bool ShouldLogStall(bool isUpstream)
    {
        lock (_stallLogLock)
        {
            var now = _lifetime.ElapsedMilliseconds;
            ref var lastLogMs = ref (isUpstream
                ? ref _lastUpstreamStallLogMs
                : ref _lastDownstreamStallLogMs);
            var shouldLog =
                lastLogMs == long.MinValue ||
                now - lastLogMs >= StallLogInterval.TotalMilliseconds;
            if (shouldLog) lastLogMs = now;
            return shouldLog;
        }
    }

    private static string ShortSegmentId(string? segmentId)
    {
        if (string.IsNullOrWhiteSpace(segmentId)) return "unknown";
        return segmentId.Length <= 160 ? segmentId : segmentId[..157] + "...";
    }
}
