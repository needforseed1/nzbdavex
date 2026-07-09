using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class PipelinedFallbackTests
{
    [Fact]
    public async Task MissingPipelineResultReleasesSingleConnectionBeforeFallback()
    {
        var created = 0;
        await using var pool = new ConnectionPool<INntpClient>(1, _ =>
        {
            Interlocked.Increment(ref created);
            return ValueTask.FromResult<INntpClient>(new MissingPipelineClient());
        });
        var provider = new MultiConnectionNntpClient(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker("test"),
            "test",
            byteLimit: null,
            bytesUsedOffset: 0,
            priority: 0,
            prepOnly: false,
            prepSpreadEnabled: true);
        using var client = new MultiProviderNntpClient([provider], new ProviderUsageTracker());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var results = new List<PipelinedStatResult>();
        await foreach (var result in client.StatsPipelinedAsync(["missing"], 1, timeout.Token))
            results.Add(result);

        Assert.Single(results);
        Assert.True(results[0].Exists);
        Assert.Equal(2, created);
    }

    private sealed class MissingPipelineClient : NntpClient
    {
        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = "223 exists",
                ArticleExists = true,
            });

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new PipelinedStatResult { SegmentId = segmentIds[0], Exists = false };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => Task.FromResult(new UsenetResponse { ResponseCode = 281, ResponseMessage = "281 authenticated" });
        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
