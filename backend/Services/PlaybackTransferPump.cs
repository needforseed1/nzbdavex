using System.Diagnostics;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Services;

internal static class PlaybackTransferPump
{
    private const int CopyBufferSize = 64 * 1024;

    /// <summary>
    /// How often a wait that is still running is reported. Waiting is the whole
    /// symptom of bad playback, so it cannot be reported only once it is over:
    /// the live view would show a healthy read for as long as it is stuck, and a
    /// wait that ends in a client abort would never be reported at all.
    /// </summary>
    private static readonly TimeSpan WaitProgressInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a single write to the client may make no progress before the
    /// request is abandoned.
    ///
    /// A player that stops reading is normally just full, so this must never
    /// fire on a paused viewer — but a client that disappears without closing
    /// its connection is indistinguishable from one that paused, and nothing
    /// below us resolves it: TCP reports no error, the reverse proxy keeps its
    /// connection because data is pending rather than idle, and the request
    /// stays pinned with its buffered segments and provider connections held.
    /// Observed in the wild at 41 minutes and still counting, ~2.9 MB wedged in
    /// the send queue of every hop.
    ///
    /// Ten minutes is far past any real pause — and a player that resumes after
    /// one simply opens a new range request, which costs it nothing.
    /// </summary>
    private static readonly TimeSpan DownstreamWriteTimeout = TimeSpan.FromMinutes(10);

    public static async Task CopyAsync(
        Stream source,
        Stream destination,
        PlaybackRequestDiagnostics diagnostics,
        long startOffset,
        long? endOffset,
        bool seekSource,
        Action<long, long>? onBytesServed,
        Action<Exception, long>? onSourceError,
        CancellationToken cancellationToken,
        TimeSpan? downstreamWriteTimeout = null)
    {
        var writeTimeout = downstreamWriteTimeout ?? DownstreamWriteTimeout;
        if (seekSource && startOffset > 0)
        {
            if (!source.CanSeek)
                throw new IOException("Cannot use range, because the source stream isn't seekable");
            source.Seek(startOffset, SeekOrigin.Begin);
        }

        var bytesToRead = endOffset - startOffset + 1 ?? long.MaxValue;
        var buffer = new byte[CopyBufferSize];
        var position = startOffset;

        while (bytesToRead > 0)
        {
            var requestedBytes = (int)Math.Min(bytesToRead, buffer.Length);
            // Sampled before each wait, so a stall reports the buffer that caused
            // it rather than the one the producer refilled while it was stuck.
            var upstreamOnset = diagnostics.CaptureBufferState();
            var readTimer = Stopwatch.StartNew();
            var upstreamReportedMs = 0L;
            int bytesRead;
            try
            {
                bytesRead = await AwaitWithWaitReportingAsync(
                        source.ReadAsync(buffer.AsMemory(0, requestedBytes), cancellationToken),
                        diagnostics,
                        isUpstream: true,
                        readTimer,
                        position,
                        upstreamOnset,
                        reportedMs => upstreamReportedMs = reportedMs)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                readTimer.Stop();
                // The wait happened whether or not it produced bytes. Reporting
                // it here is what keeps an abandoned stall — the client gave up
                // while the source was still thinking — off the "clean" pile.
                diagnostics.RecordAbandonedWait(
                    isUpstream: true,
                    readTimer.ElapsedMilliseconds,
                    upstreamReportedMs,
                    position,
                    exception is OperationCanceledException ? "cancelled" : "failed",
                    upstreamOnset);
                diagnostics.EndWait(isUpstream: true, upstreamReportedMs);
                if (exception is not OperationCanceledException) onSourceError?.Invoke(exception, position);
                throw;
            }
            diagnostics.EndWait(isUpstream: true, upstreamReportedMs);
            readTimer.Stop();
            // End of stream: the source owed nothing more, so the time spent
            // learning that is not a wait the viewer was subjected to. Counting
            // it would add a phantom stall to the tail of every request.
            if (bytesRead == 0) return;

            var downstreamOnset = diagnostics.CaptureBufferState();
            var writeTimer = Stopwatch.StartNew();
            var downstreamReportedMs = 0L;
            // Only this write is bounded, not the request: a client is free to
            // take as long as it likes overall, so long as it keeps taking.
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeDeadline.CancelAfter(writeTimeout);
            try
            {
                await AwaitWithWaitReportingAsync(
                        destination.WriteAsync(buffer.AsMemory(0, bytesRead), writeDeadline.Token),
                        diagnostics,
                        isUpstream: false,
                        writeTimer,
                        position,
                        downstreamOnset,
                        reportedMs => downstreamReportedMs = reportedMs)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                writeDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                writeTimer.Stop();
                diagnostics.RecordAbandonedWait(
                    isUpstream: false,
                    writeTimer.ElapsedMilliseconds,
                    downstreamReportedMs,
                    position,
                    "client-gone",
                    downstreamOnset);
                diagnostics.EndWait(isUpstream: false, downstreamReportedMs);
                // Not a cancellation of the request — the client abandoned it
                // without saying so. Surfaced as its own exception so the
                // session records why it ended instead of blaming the source.
                throw new DownstreamStalledException(
                    $"The client accepted no data for {writeTimeout.TotalMinutes:0.#} minutes " +
                    $"at offset {position}. Abandoning the request and releasing its segments.",
                    position,
                    writeTimer.ElapsedMilliseconds);
            }
            catch (Exception exception)
            {
                writeTimer.Stop();
                diagnostics.RecordAbandonedWait(
                    isUpstream: false,
                    writeTimer.ElapsedMilliseconds,
                    downstreamReportedMs,
                    position,
                    exception is OperationCanceledException ? "cancelled" : "failed",
                    downstreamOnset);
                diagnostics.EndWait(isUpstream: false, downstreamReportedMs);
                throw;
            }
            diagnostics.EndWait(isUpstream: false, downstreamReportedMs);
            writeTimer.Stop();

            position += bytesRead;
            diagnostics.RecordTransfer(
                bytesRead,
                position,
                readTimer.ElapsedMilliseconds,
                writeTimer.ElapsedMilliseconds,
                upstreamOnset,
                downstreamOnset,
                upstreamReportedMs,
                downstreamReportedMs);
            onBytesServed?.Invoke(bytesRead, position);
            bytesToRead -= bytesRead;
        }
    }

    private static ValueTask AwaitWithWaitReportingAsync(
        ValueTask operation,
        PlaybackRequestDiagnostics diagnostics,
        bool isUpstream,
        Stopwatch timer,
        long position,
        (int BufferedSegments, int InFlightSegments) onset,
        Action<long> onReported)
    {
        if (operation.IsCompleted) return operation;
        return new ValueTask(ReportWhileRunningAsync(
            operation.AsTask(), diagnostics, isUpstream, timer, position, onset, onReported));
    }

    private static ValueTask<int> AwaitWithWaitReportingAsync(
        ValueTask<int> operation,
        PlaybackRequestDiagnostics diagnostics,
        bool isUpstream,
        Stopwatch timer,
        long position,
        (int BufferedSegments, int InFlightSegments) onset,
        Action<long> onReported)
    {
        // The overwhelmingly common case is a read served straight from the
        // buffer. Leave it allocation-free: no task conversion, no timer.
        if (operation.IsCompleted) return operation;
        return new ValueTask<int>(ReportWhileRunningAsync(
            operation.AsTask(), diagnostics, isUpstream, timer, position, onset, onReported));
    }

    private static async Task<T> ReportWhileRunningAsync<T>(
        Task<T> operation,
        PlaybackRequestDiagnostics diagnostics,
        bool isUpstream,
        Stopwatch timer,
        long position,
        (int BufferedSegments, int InFlightSegments) onset,
        Action<long> onReported)
    {
        await ReportWhileRunningAsync(
                (Task)operation, diagnostics, isUpstream, timer, position, onset, onReported)
            .ConfigureAwait(false);
        return await operation.ConfigureAwait(false);
    }

    private static async Task ReportWhileRunningAsync(
        Task operation,
        PlaybackRequestDiagnostics diagnostics,
        bool isUpstream,
        Stopwatch timer,
        long position,
        (int BufferedSegments, int InFlightSegments) onset,
        Action<long> onReported)
    {
        var reportedMs = 0L;
        while (true)
        {
            // Do not cancel the reporting tick with the request token. A stream
            // that is slow to observe cancellation would otherwise spin through
            // already-cancelled delays until its I/O finally returned.
            var delay = Task.Delay(WaitProgressInterval);
            var finished = await Task.WhenAny(operation, delay).ConfigureAwait(false);
            if (!ReferenceEquals(finished, delay))
            {
                // Task.WhenAny reports only which task finished. Await again so
                // a delayed destination failure or cancellation is not mistaken
                // for a successful write.
                await operation.ConfigureAwait(false);
                return;
            }

            reportedMs = diagnostics.ReportWaitProgress(
                isUpstream, timer.ElapsedMilliseconds, reportedMs, position, onset);
            onReported(reportedMs);
        }
    }
}
