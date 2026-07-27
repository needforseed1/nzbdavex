using System.Runtime.CompilerServices;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

public class NzbFileStreamTests
{
    [Fact]
    public async Task FastSeekBodyFailureFallsBackToTheNormalRetryingStream()
    {
        const int segmentSize = 256;
        var client = new RecoveringSeekClient(segmentSize);
        var diagnostics = new PlaybackRequestDiagnostics(
            Guid.NewGuid(),
            "/media/test.mkv",
            "test.mkv",
            requestedRange: "bytes=64-");

        using (PlaybackDiagnosticContext.Begin(diagnostics))
        await using (var stream = new NzbFileStream(
                         ["segment-1"],
                         segmentSize,
                         client,
                         articleBufferSize: 1))
        {
            stream.Position = 64;
            var buffer = new byte[32];

            var read = await stream.ReadAsync(buffer);

            Assert.Equal(buffer.Length, read);
            Assert.All(buffer, value => Assert.Equal((byte)'A', value));
        }

        Assert.Equal(1, client.FastBodyCalls);
        Assert.Equal(1, client.HeaderCalls);
        Assert.Equal(1, client.PipelineCalls);
        Assert.Equal(1, diagnostics.Snapshot().BodyStallRecoveries);
        Assert.Equal(0, diagnostics.Snapshot().ZeroFilledSegments);
    }

    private sealed class RecoveringSeekClient(int segmentSize) : NntpClient
    {
        private readonly UsenetYencHeader _header = new()
        {
            FileName = "test.bin",
            FileSize = segmentSize,
            LineLength = 128,
            PartNumber = 0,
            TotalParts = 1,
            PartSize = segmentSize,
            PartOffset = 0,
        };

        public int FastBodyCalls { get; private set; }
        public int HeaderCalls { get; private set; }
        public int PipelineCalls { get; private set; }
        public override int PipeliningDepth => 1;

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            FastBodyCalls++;
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new StallingBody(_header),
            });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, cancellationToken);

        public override Task<UsenetYencHeader> GetYencHeadersAsync(
            string segmentId,
            CancellationToken ct)
        {
            HeaderCalls++;
            return Task.FromResult(_header);
        }

        public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            PipelineCalls++;
            foreach (var segmentId in segmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = true,
                    Stream = CompleteBody(segmentSize),
                };
            }
            await Task.CompletedTask;
        }

        private static YencStream CompleteBody(int size)
        {
            var article = new StringBuilder();
            article.Append($"=ybegin line=128 size={size} name=test.bin\r\n");
            for (var remaining = size; remaining > 0; remaining -= 128)
                article.Append(new string('k', Math.Min(128, remaining))).Append("\r\n");
            article.Append($"=yend size={size}\r\n");
            return new YencStream(
                new MemoryStream(Encoding.Latin1.GetBytes(article.ToString())));
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class StallingBody(UsenetYencHeader header) : YencStream(Stream.Null)
    {
        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<UsenetYencHeader?>(header);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(
                new BodyProgressStalledException(
                    "body stopped",
                    transferredBytes: 32,
                    providerId: "provider-1",
                    providerHost: "news.example"));
    }
}
