using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public IAsyncEnumerable<UsenetStatResponse> StatPipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
        CancellationToken cancellationToken
    )
    {
        return RunPipelinedAsync(
            segmentIds,
            depth,
            "STAT",
            (code, message, _, _) => Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = code,
                ResponseMessage = message,
                ArticleExists = code == (int)UsenetResponseType.ArticleExists,
            }),
            cancellationToken
        );
    }

    public IAsyncEnumerable<UsenetBodyResponse> BodyPipelinedAsync
    (
        IReadOnlyList<SegmentId> segmentIds,
        int depth,
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
                    body = await ReadBodyToBufferAsync(ct);
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
                    body = await ReadBodyToBufferAsync(ct);
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
        try
        {
            ThrowIfUnhealthy();
            ThrowIfNotConnected();

            var sent = 0;
            var received = 0;

            while (sent < segmentIds.Count && sent - received < depth)
            {
                await WriteLineAsync($"{command} <{segmentIds[sent]}>".AsMemory(), _cts.Token);
                sent++;
            }

            while (received < segmentIds.Count)
            {
                var segmentId = segmentIds[received];
                var status = await ReadLineAsync(_cts.Token);
                var code = ParseResponseCode(status);
                var result = await readResponse(code, status!, segmentId, _cts.Token);
                received++;

                if (sent < segmentIds.Count)
                {
                    await WriteLineAsync($"{command} <{segmentIds[sent]}>".AsMemory(), _cts.Token);
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

    private async Task<MemoryStream> ReadBodyToBufferAsync(CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var scratch = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            while (true)
            {
                var line = await ReadLineAsync(cancellationToken);
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
}
