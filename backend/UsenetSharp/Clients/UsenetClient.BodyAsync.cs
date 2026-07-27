using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public Task<UsenetBodyResponse> BodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return BodyAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetBodyResponse> BodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await _commandLock.WaitAsync(cancellationToken);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        var isReadBodyToPipeAsyncStarted = false;
        var operationCts = CreateOperationCts(cancellationToken);
        var operationToken = operationCts.Token;

        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            // Send BODY command with message-id
            await WriteLineAsync($"BODY <{segmentId}>".AsMemory(), operationToken);
            var response = await ReadLineAsync(operationToken);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - body follows
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedBodyFollows)
            {
                // Create a pipe for streaming the body data
                var pipe = new Pipe(new PipeOptions(
                    pauseWriterThreshold: StreamingPipePauseThreshold,
                    resumeWriterThreshold: StreamingPipeResumeThreshold,
                    useSynchronizationContext: false
                ));

                // Start background task to read the body and write to pipe
                isReadBodyToPipeAsyncStarted = true;
                _ = ReadBodyToPipeAsync(pipe.Writer, operationToken, (articleBodyResult, failure) =>
                {
                    // Complete with the failure so the reader sees a fault instead of
                    // an ordinary EOF. A silent EOF would hand the caller a truncated
                    // body that looks successful, shifting every later byte of the file.
                    pipe.Writer.Complete(failure);
                    _commandLock.Release();
                    onConnectionReadyAgain?.Invoke(articleBodyResult);
                    operationCts.Dispose();
                });

                // Return immediately with the stream and headers
                return new UsenetBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    Stream = pipe.Reader.AsStream(),
                };
            }

            return new UsenetBodyResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                Stream = null
            };
        }
        finally
        {
            if (!isReadBodyToPipeAsyncStarted)
            {
                operationCts.Dispose();
                _commandLock.Release();
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            }
        }
    }

    private async Task ReadBodyToPipeAsync(
        PipeWriter writer,
        CancellationToken cancellationToken,
        Action<ArticleBodyResult, Exception?> onFinally)
    {
        var completed = false;
        Exception? failure = null;
        try
        {
            if (_reader == null)
            {
                failure = new UsenetNotConnectedException(
                    "The connection was closed before its body could be read.");
                return;
            }

            var shouldWrite = true;

            // Read lines until we encounter the termination sequence (single dot on a line)
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await ReadLineAsync(cancellationToken);

                if (line == null)
                {
                    // The server closed the socket before sending the terminating
                    // dot, so whatever reached the pipe is an incomplete body.
                    failure = new UsenetProtocolException(
                        "The connection ended before the article body was complete.");
                    break;
                }

                // Check for NNTP termination sequence (single dot)
                if (line == ".")
                {
                    completed = true;
                    break;
                }

                if (!shouldWrite) continue;

                // NNTP escaping: Lines starting with ".." should have the first dot removed
                // Use ReadOnlySpan to avoid string allocation from Substring
                ReadOnlySpan<char> lineSpan = line.AsSpan();
                if (lineSpan.Length >= 2 && lineSpan[0] == '.' && lineSpan[1] == '.')
                {
                    lineSpan = lineSpan.Slice(1);
                }

                // Write the line to the pipe using Latin1 to preserve byte values 0-255
                var byteCount = Encoding.Latin1.GetByteCount(lineSpan) + 2; // +2 for CRLF
                var span = writer.GetSpan(byteCount);
                var written = Encoding.Latin1.GetBytes(lineSpan, span);
                span[written++] = (byte)'\r';
                span[written++] = (byte)'\n';
                writer.Advance(written);

                // Flush periodically to make data available for reading
                var result = await RunWithTimeoutAsync(writer.FlushAsync, cancellationToken);
                if (result.IsCompleted || result.IsCanceled)
                {
                    shouldWrite = false;
                }
            }

            if (!completed && failure == null)
                failure = new OperationCanceledException(
                    "The article body was cancelled before it was complete.", cancellationToken);
        }
        catch (Exception e)
        {
            failure = e;
            lock (this)
            {
                _backgroundException = ExceptionDispatchInfo.Capture(e);
            }
        }
        finally
        {
            onFinally.Invoke(
                completed ? ArticleBodyResult.Retrieved : ArticleBodyResult.NotRetrieved,
                completed ? null : failure);
        }
    }
}
