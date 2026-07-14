using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class QueueConnectionWarmerTests
{
    [Theory]
    [InlineData(ProviderType.HealthChecksOnly, 40, 20)]
    [InlineData(ProviderType.HealthChecksOnly, 95, 47)]
    [InlineData(ProviderType.HealthChecksOnly, 1, 1)]
    [InlineData(ProviderType.Pooled, 100, 16)]
    [InlineData(ProviderType.BackupAndStats, 50, 25)]
    [InlineData(ProviderType.BackupAndStats, 49, 24)]
    [InlineData(ProviderType.BackupOnly, 50, 0)]
    [InlineData(ProviderType.Disabled, 50, 0)]
    public void DedicatedHealthProvidersKeepHalfTheirPoolWarm(
        ProviderType providerType,
        int maxConnections,
        int expected)
    {
        Assert.Equal(expected,
            UsenetStreamingClient.GetWarmConnectionTarget(providerType, maxConnections));
    }

    [Fact]
    public async Task DistributesTargetAcrossPooledProvidersByCapacity()
    {
        using var largePool = CreateProvider(ProviderType.Pooled, "large", 4, priority: 0);
        using var smallPool = CreateProvider(ProviderType.Pooled, "small", 2, priority: 1);
        using var backup = CreateProvider(ProviderType.BackupOnly, "backup", 5, priority: 2);
        using var client = new MultiProviderNntpClient(
            [largePool, smallPool, backup], new ProviderUsageTracker());

        await client.PrewarmQueueAsync(3, CancellationToken.None);

        Assert.Equal(2, largePool.LiveConnections);
        Assert.Equal(1, smallPool.LiveConnections);
        Assert.Equal(0, backup.LiveConnections);
    }

    [Fact]
    public async Task HealthPrewarmFillsHalfOfDedicatedHealthProvidersOnly()
    {
        using var pooled = CreateProvider(ProviderType.Pooled, "pooled", 6, priority: 0);
        using var healthOnly = CreateProvider(ProviderType.HealthChecksOnly, "farm", 4, priority: 1);
        using var backupAndStats = CreateProvider(ProviderType.BackupAndStats, "block", 3, priority: 2);
        using var backupOnly = CreateProvider(ProviderType.BackupOnly, "backup", 5, priority: 3);
        using var client = new MultiProviderNntpClient(
            [pooled, healthOnly, backupAndStats, backupOnly], new ProviderUsageTracker());

        await client.PrewarmHealthCheckAsync(CancellationToken.None);

        Assert.Equal(0, pooled.LiveConnections);
        Assert.Equal(2, healthOnly.LiveConnections);
        Assert.Equal(1, backupAndStats.LiveConnections);
        Assert.Equal(0, backupOnly.LiveConnections);
    }

    private static MultiConnectionNntpClient CreateProvider(
        ProviderType type, string host, int maxConnections, int priority)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections, _ => ValueTask.FromResult<INntpClient>(new StubNntpClient()));
        return new MultiConnectionNntpClient(
            pool,
            type,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority,
            prepOnly: false,
            prepSpreadEnabled: true);
    }

    private sealed class StubNntpClient : NntpClient
    {
        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
