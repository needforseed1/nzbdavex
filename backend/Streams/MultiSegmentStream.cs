using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream
{
    internal const int MaxBodyRetries = 2;

    private readonly Memory<string> _segmentIds;
    private readonly INntpClient _usenetClient;
    private readonly long _expectedSegmentSize;
    private readonly bool _failFastOnFirstSegment;
    private readonly int _pipeliningDepth;
    private readonly Channel<Task<Stream>> _streamTasks;
    private readonly ContextualCancellationTokenSource _cts;
    private readonly PlaybackRequestDiagnostics? _playbackDiagnostics;
    private Stream? _stream;
    private bool _disposed;

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        long expectedSegmentSize,
        bool failFastOnFirstSegment,
        CancellationToken cancellationToken
    )
    {
        if (articleBufferSize == 0)
            return new UnbufferedMultiSegmentStream(segmentIds, usenetClient, expectedSegmentSize);

        return new MultiSegmentStream(segmentIds, usenetClient, articleBufferSize, usenetClient.PipeliningDepth,
            expectedSegmentSize, failFastOnFirstSegment, cancellationToken);
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        int pipeliningDepth,
        long expectedSegmentSize,
        bool failFastOnFirstSegment,
        CancellationToken cancellationToken
    )
    {
        _segmentIds = segmentIds;
        _usenetClient = usenetClient;
        _pipeliningDepth = pipeliningDepth;
        _expectedSegmentSize = expectedSegmentSize;
        _failFastOnFirstSegment = failFastOnFirstSegment;
        _streamTasks = Channel.CreateBounded<Task<Stream>>(articleBufferSize);
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _playbackDiagnostics = PlaybackDiagnosticContext.Current;
        _ = pipeliningDepth > 0
            ? DownloadSegmentsPipelined(pipeliningDepth, _cts.Token)
            : DownloadSegments(_cts.Token);
    }

    private async Task DownloadSegments(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            for (var i = 0; i < _segmentIds.Length; i++)
            {
                var segmentId = _segmentIds.Span[i];

                await _streamTasks.Writer.WaitToWriteAsync(cancellationToken);
                var connection = await _usenetClient.AcquireExclusiveConnectionAsync(segmentId, cancellationToken);
                var streamTask = DownloadSegmentTracked(
                    segmentId, connection, isFirstSegment: i == 0, cancellationToken);
                if (_streamTasks.Writer.TryWrite(streamTask)) continue;

                // if we never get a chance to write the stream to the writer
                // then make sure the stream gets disposed.
                _ = DisposeBufferedTaskAsync(streamTask);
                break;
            }
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            failure = e;
        }
        finally
        {
            _streamTasks.Writer.TryComplete(failure);
        }

        return;
    }

    private async Task<Stream> DownloadSegmentTracked(
        string segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        bool isFirstSegment,
        CancellationToken cancellationToken)
    {
        _playbackDiagnostics?.UpstreamOperationStarted();
        try
        {
            var stream = await DownloadSegment(
                    segmentId, exclusiveConnection, isFirstSegment, cancellationToken)
                .ConfigureAwait(false);
            _playbackDiagnostics?.SegmentBuffered();
            return stream;
        }
        finally
        {
            _playbackDiagnostics?.UpstreamOperationCompleted();
        }
    }

    private async Task<Stream> DownloadSegment
    (
        string segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        bool isFirstSegment,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var bodyResponse = attempt == 0
                    ? await _usenetClient
                        .DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken)
                        .ConfigureAwait(false)
                    : await _usenetClient
                        .DecodedBodyAsync(segmentId, cancellationToken)
                        .ConfigureAwait(false);

                return await DrainSegmentAsync(bodyResponse.Stream, cancellationToken).ConfigureAwait(false);
            }
            catch (UsenetArticleNotFoundException e)
            {
                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    Log.Warning(e, "First article {SegmentId} missing on all providers at playback start. " +
                                   "Failing the stream so the player surfaces an error.", segmentId);
                    throw;
                }

                return ZeroFillSegment(
                    "Article {SegmentId} missing on all providers. Zero-filling {Bytes} bytes to keep playback alive.",
                    e.SegmentId,
                    e);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt < MaxBodyRetries)
                {
                    ReportRetry(segmentId, e, attempt + 1);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (_failFastOnFirstSegment && isFirstSegment)
                {
                    Log.Warning(e, "Segment {SegmentId} unavailable at playback start after {Attempts} attempts. " +
                                   "Failing the stream so the player surfaces an error.", segmentId, attempt + 1);
                    throw;
                }

                return ZeroFillSegment(
                    "Segment {SegmentId} unavailable after retries. Zero-filling {Bytes} bytes to keep playback alive.",
                    segmentId, e);
            }
        }
    }

    private async Task DownloadSegmentsPipelined(int depth, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        _playbackDiagnostics?.UpstreamOperationStarted();
        try
        {
            var segmentIds = _segmentIds.ToArray();
            var index = 0;
            await foreach (var result in _usenetClient.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await _streamTasks.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
                var isFirstSegment = index == 0;
                index++;
                var streamTask = MaterializeSegmentTracked(result, isFirstSegment, cancellationToken);
                if (_streamTasks.Writer.TryWrite(streamTask)) continue;

                _ = DisposeBufferedTaskAsync(streamTask);
                break;
            }
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            failure = e;
        }
        finally
        {
            _streamTasks.Writer.TryComplete(failure);
            _playbackDiagnostics?.UpstreamOperationCompleted();
        }
    }

    private async Task<Stream> MaterializeSegmentTracked(
        PipelinedBodyResult result,
        bool isFirstSegment,
        CancellationToken cancellationToken)
    {
        var stream = await MaterializeSegment(result, isFirstSegment, cancellationToken)
            .ConfigureAwait(false);
        _playbackDiagnostics?.SegmentBuffered();
        return stream;
    }

    private async Task<Stream> MaterializeSegment(PipelinedBodyResult result, bool isFirstSegment,
        CancellationToken cancellationToken)
    {
        if (result is { Found: true, Stream: not null })
        {
            try
            {
                return await DrainSegmentAsync(result.Stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                if (_failFastOnFirstSegment && isFirstSegment) throw;
                // A body that started and then broke — a wedged socket, or a part
                // that decoded short — is refetchable. Substituting zeros on the
                // first failure corrupts the file for a fault the sequential path
                // routinely recovers from on the next attempt.
                return await RefetchSegmentAsync(result.SegmentId, e, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (_failFastOnFirstSegment && isFirstSegment)
            throw new UsenetArticleNotFoundException(result.SegmentId);

        return ZeroFillSegment(
            "Article {SegmentId} missing on all providers. Zero-filling {Bytes} bytes to keep playback alive.",
            result.SegmentId);
    }

    /// <summary>
    /// Fetches a segment again outside the pipeline after its pipelined body
    /// failed to materialize, giving it the same retry budget the sequential
    /// path has before any zeros are served.
    /// </summary>
    private async Task<Stream> RefetchSegmentAsync(
        string segmentId,
        Exception cause,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= MaxBodyRetries; attempt++)
        {
            ReportRetry(segmentId, cause, attempt);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);
                var response = await _usenetClient
                    .DecodedBodyAsync(segmentId, cancellationToken)
                    .ConfigureAwait(false);
                if (response.Stream is null)
                    return ZeroFillSegment(
                        "Article {SegmentId} missing on all providers. Zero-filling {Bytes} bytes to keep playback alive.",
                        segmentId);
                return await DrainSegmentAsync(response.Stream, cancellationToken).ConfigureAwait(false);
            }
            catch (UsenetArticleNotFoundException e)
            {
                return ZeroFillSegment(
                    "Article {SegmentId} missing on all providers. Zero-filling {Bytes} bytes to keep playback alive.",
                    segmentId,
                    e);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                cause = e;
            }
        }

        return ZeroFillSegment(
            "Segment {SegmentId} failed to materialize. Zero-filling {Bytes} bytes to keep playback alive.",
            segmentId, cause);
    }

    /// <summary>
    /// A retry is the stream recovering from something. Silence here is how a
    /// wedged provider socket becomes invisible the moment it heals, so the body
    /// watchdog's catches are counted on the session rather than only logged.
    /// </summary>
    private void ReportRetry(string segmentId, Exception cause, int attempt)
    {
        if (cause is BodyProgressStalledException stalled)
        {
            _playbackDiagnostics?.RecordBodyStallRecovery(
                stalled.ProviderId,
                stalled.ProviderHost,
                segmentId,
                stalled.TransferredBytes,
                attempt);
            if (_playbackDiagnostics is null)
                Log.Debug(cause, "Body for segment {SegmentId} stalled (attempt {Attempt}). Refetching.",
                    segmentId, attempt);
            return;
        }

        Log.Debug(cause, "Transient failure fetching segment {SegmentId} (attempt {Attempt}). Retrying.",
            segmentId, attempt);
    }

    private Stream ZeroFillSegment(string messageTemplate, string segmentId, Exception? exception = null)
    {
        var fill = _expectedSegmentSize > 0 ? _expectedSegmentSize : 1;
        if (_playbackDiagnostics is not null)
        {
            // One structured Warning is enough. Logging here as well would emit
            // two warnings for every missing article.
            _playbackDiagnostics.RecordZeroFill(segmentId, fill, exception);
        }
        else if (exception == null)
            Log.Warning(messageTemplate, segmentId, fill);
        else
            Log.Warning(exception, messageTemplate, segmentId, fill);
        return new MemoryStream(new byte[fill], writable: false);
    }

    private async Task<Stream> DrainSegmentAsync(Stream source, CancellationToken cancellationToken)
    {
        try
        {
            var capacity = _expectedSegmentSize is > 0 and <= int.MaxValue ? (int)_expectedSegmentSize : 0;
            var buffer = new MemoryStream(capacity);
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            return buffer;
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (!cancellationToken.IsCancellationRequested)
        {
            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (!await _streamTasks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_streamTasks.Reader.TryRead(out var streamTask)) return 0;
                _stream = await streamTask;
                _playbackDiagnostics?.SegmentDequeued();
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync();
            _stream = null;
        }

        return 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _stream?.Dispose();
        _streamTasks.Writer.TryComplete();

        // ensure that streams that were never read from the channel get disposed
        while (_streamTasks.Reader.TryRead(out var streamTask))
            _ = DisposeBufferedTaskAsync(streamTask);

        base.Dispose();
    }

    private async Task DisposeBufferedTaskAsync(Task<Stream> streamTask)
    {
        try
        {
            var stream = await streamTask.ConfigureAwait(false);
            _playbackDiagnostics?.SegmentDequeued();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The producer completes the channel with fetch failures. Cleanup
            // must not create a second unobserved task fault.
        }
    }
}
