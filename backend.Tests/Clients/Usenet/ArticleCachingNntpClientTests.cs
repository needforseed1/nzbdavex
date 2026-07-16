using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ArticleCachingNntpClientTests
{
    [Fact]
    public async Task FirstSegmentProbeBypassesFullArticleCache()
    {
        const int articleSize = 256 * 1024;
        using var inner = new PrefixTrackingClient(articleSize);
        using var cache = new ArticleCachingNntpClient(inner);
        var file = new NzbFile { Subject = "first.rar" };
        file.Segments.Add(new NzbSegment { Bytes = articleSize, MessageId = "first-segment" });

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            [file], cache, new ConfigManager(), CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(16 * 1024, results[0].First16KB?.Length);
        Assert.Equal(16 * 1024, inner.BytesRead);
    }

    [Fact]
    public async Task FirstSegmentProbeSkipsPar2RecoveryVolumesButKeepsBaseIndex()
    {
        using var inner = new PrefixTrackingClient(32 * 1024);
        var files = new[]
        {
            File("release.rar", "rar"),
            File("release.par2", "par-index"),
            File("release.vol00+01.par2", "par-volume-plus"),
            File("release.vol01-03.par2", "par-volume-hyphen"),
        };

        var results = await FetchFirstSegmentsStep.FetchFirstSegments(
            [.. files], inner, new ConfigManager(), CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(2, inner.ArticleCalls);
        Assert.False(ResultFor("release.rar").MissingFirstSegment);
        Assert.False(ResultFor("release.par2").MissingFirstSegment);
        Assert.True(ResultFor("release.vol00+01.par2").MissingFirstSegment);
        Assert.True(ResultFor("release.vol01-03.par2").MissingFirstSegment);

        FetchFirstSegmentsStep.NzbFileWithFirstSegment ResultFor(string subject) =>
            Assert.Single(results, result => result.NzbFile.Subject == subject);
    }

    [Fact]
    public async Task FirstSegmentProbeDoesNotSkipAmbiguousPar2Name()
    {
        using var inner = new PrefixTrackingClient(32 * 1024);
        var file = File("obfuscated.par2", "ambiguous-par2");

        var result = Assert.Single(await FetchFirstSegmentsStep.FetchFirstSegments(
            [file], inner, new ConfigManager(), CancellationToken.None));

        Assert.Equal(1, inner.ArticleCalls);
        Assert.False(result.MissingFirstSegment);
    }

    [Fact]
    public async Task HeaderProbeBypassesFullArticleCache()
    {
        using var inner = new HeaderOnlyClient();
        using var cache = new ArticleCachingNntpClient(inner);

        var header = await cache.GetYencHeadersAsync("last-segment", CancellationToken.None);

        Assert.Equal(750_000, header.PartSize);
        Assert.Equal(1, inner.HeaderCalls);
        Assert.Equal(0, inner.BodyCalls);
    }

    private sealed class HeaderOnlyClient : NntpClient
    {
        public int HeaderCalls { get; private set; }
        public int BodyCalls { get; private set; }

        public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
        {
            HeaderCalls++;
            return Task.FromResult(new UsenetYencHeader
            {
                FileName = "file.bin",
                FileSize = 1_500_000,
                LineLength = 128,
                PartNumber = 2,
                TotalParts = 2,
                PartSize = 750_000,
                PartOffset = 750_000,
            });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            BodyCalls++;
            throw new InvalidOperationException("Header probes must not fetch a cached body.");
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) => DecodedBodyAsync(segmentId, cancellationToken);

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class PrefixTrackingClient(int articleSize) : NntpClient
    {
        private int _bytesRead;
        private int _articleCalls;
        public int BytesRead => Volatile.Read(ref _bytesRead);
        public int ArticleCalls => Volatile.Read(ref _articleCalls);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _articleCalls);
            var header = new UsenetYencHeader
            {
                FileName = "first.rar",
                FileSize = articleSize,
                LineLength = 128,
                PartNumber = 1,
                TotalParts = 1,
                PartSize = articleSize,
                PartOffset = 0,
            };
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article follows",
                ArticleHeaders = new UsenetArticleHeader { Headers = [] },
                Stream = new CachedYencStream(header, new CountingStream(
                    new MemoryStream(new byte[articleSize]), count => Interlocked.Add(ref _bytesRead, count))),
            });
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }

    private static NzbFile File(string subject, string messageId)
    {
        var file = new NzbFile { Subject = subject };
        file.Segments.Add(new NzbSegment { Bytes = 32 * 1024, MessageId = messageId });
        return file;
    }

    private sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            onRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            onRead(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
