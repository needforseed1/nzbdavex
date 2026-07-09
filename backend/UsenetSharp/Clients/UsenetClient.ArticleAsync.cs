using System.IO.Pipelines;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private const int StreamingPipePauseThreshold = 64 * 1024;
    private const int StreamingPipeResumeThreshold = 32 * 1024;

    public Task<UsenetArticleResponse> ArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return ArticleAsync(segmentId, null, cancellationToken);
    }

    public async Task<UsenetArticleResponse> ArticleAsync
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

            // Send ARTICLE command with message-id
            await WriteLineAsync($"ARTICLE <{segmentId}>".AsMemory(), operationToken);
            var response = await ReadLineAsync(operationToken);
            var responseCode = ParseResponseCode(response);

            // Article retrieved - head and body follow
            if (responseCode == (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            {
                // Parse headers
                var headers = await ParseArticleHeadersAsync(operationToken);

                // Create a pipe for streaming the body data
                var pipe = new Pipe(new PipeOptions(
                    pauseWriterThreshold: StreamingPipePauseThreshold,
                    resumeWriterThreshold: StreamingPipeResumeThreshold,
                    useSynchronizationContext: false
                ));

                // Start background task to read the body and write to pipe
                isReadBodyToPipeAsyncStarted = true;
                _ = ReadBodyToPipeAsync(pipe.Writer, operationToken, articleBodyResult =>
                {
                    pipe.Writer.Complete();
                    _commandLock.Release();
                    onConnectionReadyAgain?.Invoke(articleBodyResult);
                    operationCts.Dispose();
                });

                // Return immediately with the stream and headers
                return new UsenetArticleResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = responseCode,
                    ResponseMessage = response!,
                    ArticleHeaders = headers,
                    Stream = pipe.Reader.AsStream(),
                };
            }

            return new UsenetArticleResponse()
            {
                ResponseCode = responseCode,
                ResponseMessage = response!,
                SegmentId = segmentId,
                Stream = null,
                ArticleHeaders = null
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

    private async Task<UsenetArticleHeader> ParseArticleHeadersAsync(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? currentHeaderName = null;
        var currentHeaderValue = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await ReadLineAsync(cancellationToken);

            if (line == null)
            {
                throw new UsenetProtocolException("Invalid NNTP response: missing article headers.");
            }

            // Empty line signals end of headers
            if (string.IsNullOrEmpty(line) || line == ".")
            {
                // Save the last header if any
                if (currentHeaderName != null)
                {
                    headers[currentHeaderName] = currentHeaderValue.ToString().Trim();
                }

                break;
            }

            // Check if this is a continuation line (starts with whitespace)
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                // Append to current header value
                if (currentHeaderName != null)
                {
                    currentHeaderValue.Append(' ');
                    currentHeaderValue.Append(line.Trim());
                }
            }
            else
            {
                // Save the previous header if any
                if (currentHeaderName != null)
                {
                    headers[currentHeaderName] = currentHeaderValue.ToString().Trim();
                }

                // Parse new header: "Name: Value"
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    currentHeaderName = line.Substring(0, colonIndex).Trim();
                    currentHeaderValue.Clear();

                    // Get value after colon
                    if (colonIndex + 1 < line.Length)
                    {
                        currentHeaderValue.Append(line.Substring(colonIndex + 1).Trim());
                    }
                }
            }
        }

        return new UsenetArticleHeader
        {
            Headers = headers
        };
    }
}
