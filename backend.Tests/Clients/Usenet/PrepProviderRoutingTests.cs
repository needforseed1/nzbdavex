using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class PrepProviderRoutingTests
{
    [Fact]
    public async Task PrepSkipsZeroLiveProviderWhenAnotherPoolIsReady()
    {
        var stalledFactoryCalls = 0;
        var stalledRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var stalledPool = new ConnectionPool<INntpClient>(
            4,
            async ct =>
            {
                Interlocked.Increment(ref stalledFactoryCalls);
                await stalledRelease.Task.WaitAsync(ct);
                return new BodyClient();
            });
        using var stalled = CreateProvider(stalledPool, "stalled", priority: 0);

        var readyTransport = new BodyClient();
        await using var readyPool = new ConnectionPool<INntpClient>(
            4, _ => ValueTask.FromResult<INntpClient>(readyTransport));
        await readyPool.PrewarmAsync(4);
        using var ready = CreateProvider(readyPool, "ready", priority: 1);
        using var client = new MultiProviderNntpClient([stalled, ready], new ProviderUsageTracker());

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            client.DecodedBodyAsync($"segment-{index}", CancellationToken.None)));
        foreach (var response in responses)
            await response.Stream.DisposeAsync();

        Assert.Equal(0, Volatile.Read(ref stalledFactoryCalls));
        Assert.Equal(8, readyTransport.BodyCalls);
    }

    private static MultiConnectionNntpClient CreateProvider(
        ConnectionPool<INntpClient> pool, string host, int priority) =>
        new(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority,
            prepOnly: false,
            prepSpreadEnabled: true);

    private sealed class BodyClient : NntpClient
    {
        private int _bodyCalls;
        public int BodyCalls => Volatile.Read(ref _bodyCalls);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _bodyCalls);
            callback?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new YencStream(new MemoryStream()),
            });
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
