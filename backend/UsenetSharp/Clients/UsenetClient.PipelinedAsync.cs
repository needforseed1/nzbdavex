using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private static readonly TimeSpan BulkStatResponseInactivityTimeout =
        TimeSpan.FromSeconds(2);

    public IAsyncEnumerable<UsenetStatResponse> StatPipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
        CancellationToken cancellationToken
    )
    {
        return RunBulkStatPipelinedAsync(segmentIds, depth, cancellationToken);
    }

    private async IAsyncEnumerable<UsenetStatResponse> RunBulkStatPipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) yield break;
        depth = Math.Clamp(depth, 1, segmentIds.Count);

        await _commandLock.WaitAsync(cancellationToken);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var operationToken = linkedCts.Token;
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            for (var offset = 0; offset < segmentIds.Count; offset += depth)
            {
                operationToken.ThrowIfCancellationRequested();
                var count = Math.Min(depth, segmentIds.Count - offset);
                BulkStatBatchResult batch;
                try
                {
                    batch = await ExecuteBulkStatBatchAsync(
                            segmentIds, offset, count, operationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    MarkUnhealthy(e);
                    throw;
                }

                if (batch.Failure is not null)
                    MarkUnhealthy(batch.Failure.SourceException);

                // Buffer the complete batch before yielding. This removes an
                // async-iterator hop per STAT response and means a caller can
                // stop after any healthy result without leaving unread replies
                // behind. On a stalled batch, expose the valid prefix before
                // reporting the failure so fallback retries only the unread tail.
                foreach (var response in batch.Responses)
                    yield return response;

                batch.Failure?.Throw();
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<BulkStatBatchResult> ExecuteBulkStatBatchAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int offset,
        int count,
        CancellationToken cancellationToken
    )
    {
        var payload = new StringBuilder(count * 48);
        for (var i = 0; i < count; i++)
            payload.Append("STAT <").Append((string)segmentIds[offset + i]).Append(">\r\n");

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(BulkStatResponseInactivityTimeout);
        var token = idleCts.Token;
        var responses = new UsenetStatResponse[count];
        var received = 0;
        try
        {
            var commandText = payload.ToString();
            var byteCount = Encoding.Latin1.GetByteCount(commandText);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = Encoding.Latin1.GetBytes(commandText.AsSpan(), buffer.AsSpan());
                await _stream!.WriteAsync(buffer.AsMemory(0, written), token).ConfigureAwait(false);
                await _stream.FlushAsync(token).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            for (; received < count; received++)
            {
                var response = await _reader!.ReadLineAsync(token).ConfigureAwait(false);
                idleCts.CancelAfter(BulkStatResponseInactivityTimeout);
                if (response is null)
                    throw new UsenetProtocolException(
                        "Connection closed while reading pipelined STAT responses.");

                var responseCode = ParseResponseCode(response);
                var articleExists = responseCode == (int)UsenetResponseType.ArticleExists;
                if (articleExists && !ResponseEchoesSegmentId(
                        response, segmentIds[offset + received]))
                    throw new UsenetProtocolException(
                        "Pipelined STAT responses are out of order; aborting batch.");

                responses[received] = new UsenetStatResponse
                {
                    ResponseCode = responseCode,
                    ResponseMessage = response,
                    ArticleExists = articleExists,
                };
            }

            return new BulkStatBatchResult(responses, null);
        }
        catch (OperationCanceledException e) when (
            idleCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var failure = new UsenetPipelinedStatStalledException(
                $"Pipelined STAT batch stopped producing responses for " +
                $"{BulkStatResponseInactivityTimeout.TotalSeconds:0.###} seconds " +
                $"after {received} of {count} responses.",
                received,
                e);
            return new BulkStatBatchResult(
                responses.AsSpan(0, received).ToArray(),
                ExceptionDispatchInfo.Capture(failure));
        }
    }

    private sealed record BulkStatBatchResult(
        UsenetStatResponse[] Responses,
        ExceptionDispatchInfo? Failure);

    private void MarkUnhealthy(Exception exception)
    {
        lock (this)
            _backgroundException ??= ExceptionDispatchInfo.Capture(exception);
    }

    private static bool ResponseEchoesSegmentId(string response, SegmentId segmentId)
    {
        var id = (string)segmentId;
        return string.IsNullOrEmpty(id)
               || response.AsSpan().IndexOf(id.AsSpan(), StringComparison.Ordinal) >= 0;
    }

    public IAsyncEnumerable<UsenetBodyResponse> BodyPipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
        CancellationToken cancellationToken
    ) => BodyPipelinedAsync(segmentIds, depth, bodyReadInactivityTimeout: null, cancellationToken);

    public IAsyncEnumerable<UsenetBodyResponse> BodyPipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
        TimeSpan? bodyReadInactivityTimeout,
        CancellationToken cancellationToken
    )
    {
        return RunPipelinedAsync(
            segmentIds,
            depth,
            "BODY",
            async (code, message, segmentId, ct) =>
            {
                Stream? body = null;
                if (code == (int)UsenetResponseType.ArticleRetrievedBodyFollows)
                    body = await ReadBodyToBufferAsync(ct, bodyReadInactivityTimeout);
                return new UsenetBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = code,
                    ResponseMessage = message,
                    Stream = body,
                };
            },
            cancellationToken
        );
    }

    public IAsyncEnumerable<UsenetArticleResponse> ArticlePipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
        CancellationToken cancellationToken
    )
    {
        return RunPipelinedAsync(
            segmentIds,
            depth,
            "ARTICLE",
            async (code, message, segmentId, ct) =>
            {
                UsenetArticleHeader? headers = null;
                Stream? body = null;
                if (code == (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                {
                    headers = await ParseArticleHeadersAsync(ct);
                    body = await ReadBodyToBufferAsync(ct, bodyReadInactivityTimeout: null);
                }

                return new UsenetArticleResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = code,
                    ResponseMessage = message,
                    ArticleHeaders = headers,
                    Stream = body,
                };
            },
            cancellationToken
        );
    }

    private async IAsyncEnumerable<T> RunPipelinedAsync<T>
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
        string command,
        Func<int, string, SegmentId, CancellationToken, Task<T>> readResponse,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (segmentIds.Count == 0) yield break;
        if (depth < 1) depth = 1;

        await _commandLock.WaitAsync(cancellationToken);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var operationToken = linkedCts.Token;
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            var sent = 0;
            var received = 0;

            while (sent < segmentIds.Count && sent - received < depth)
            {
                operationToken.ThrowIfCancellationRequested();
                await WriteLineAsync($"{command} <{segmentIds[sent]}>".AsMemory(), operationToken);
                sent++;
            }

            while (received < segmentIds.Count)
            {
                operationToken.ThrowIfCancellationRequested();
                var segmentId = segmentIds[received];
                var status = await ReadLineAsync(operationToken);
                var code = ParseResponseCode(status);
                var result = await readResponse(code, status!, segmentId, operationToken);
                received++;

                if (sent < segmentIds.Count)
                {
                    operationToken.ThrowIfCancellationRequested();
                    await WriteLineAsync($"{command} <{segmentIds[sent]}>".AsMemory(), operationToken);
                    sent++;
                }

                yield return result;
            }
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<MemoryStream> ReadBodyToBufferAsync(
        CancellationToken cancellationToken,
        TimeSpan? bodyReadInactivityTimeout)
    {
        var buffer = new MemoryStream();
        var scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var line = bodyReadInactivityTimeout is null
                    ? await ReadLineAsync(cancellationToken)
                    : await ReadLineWithInactivityDeadlineAsync(
                        bodyReadInactivityTimeout.Value, buffer.Length, cancellationToken);
                if (line == null) break;
                if (line == ".") break;

                var lineSpan = line.AsSpan();
                if (lineSpan.Length >= 2 && lineSpan[0] == '.' && lineSpan[1] == '.')
                    lineSpan = lineSpan[1..];

                var needed = Encoding.Latin1.GetByteCount(lineSpan) + 2;
                if (needed > scratch.Length)
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                    scratch = ArrayPool<byte>.Shared.Rent(needed);
                }

                var written = Encoding.Latin1.GetBytes(lineSpan, scratch);
                scratch[written++] = (byte)'\r';
                scratch[written++] = (byte)'\n';
                buffer.Write(scratch, 0, written);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        buffer.Position = 0;
        return buffer;
    }

    private async ValueTask<string?> ReadLineWithInactivityDeadlineAsync(
        TimeSpan inactivityTimeout,
        long transferredBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(inactivityTimeout);
        try
        {
            return await ReadLineAsync(deadline.Token);
        }
        catch (OperationCanceledException e) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ReadInactivityTimeoutException(
                $"The pipelined article body delivered no bytes for " +
                $"{inactivityTimeout.TotalSeconds:0.###} seconds.",
                e,
                transferredBytes);
        }
    }
}
