using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ArticleNotFoundConnectionTests
{
    [Fact]
    public async Task CleanArticleNotFoundFailsOnceAndKeepsConnectionWarm()
    {
        var connection = new NotFoundNntpClient();
        await using var pool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(connection));
        using var provider = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("provider"),
            "provider",
            byteLimit: null,
            bytesUsedOffset: 0,
            priority: 0,
            prepOnly: false,
            prepSpreadEnabled: true);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            () => provider.DecodedArticleAsync("missing", CancellationToken.None));

        Assert.Equal(1, connection.ArticleCalls);
        Assert.Equal(1, provider.LiveConnections);
        Assert.Equal(1, provider.IdleConnections);
    }

    private sealed class NotFoundNntpClient : NntpClient
    {
        public int ArticleCalls { get; private set; }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            ArticleCalls++;
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            return Task.FromException<UsenetDecodedArticleResponse>(
                new UsenetArticleNotFoundException(segmentId));
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
            SegmentId segmentId, Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
