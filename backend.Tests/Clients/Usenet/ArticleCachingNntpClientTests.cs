using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ArticleCachingNntpClientTests
{
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
}
