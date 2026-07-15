using System.Runtime.CompilerServices;
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
    [InlineData(ProviderType.HealthChecksOnly, 40, 36)]
    [InlineData(ProviderType.HealthChecksOnly, 95, 86)]
    [InlineData(ProviderType.HealthChecksOnly, 1, 1)]
    [InlineData(ProviderType.Pooled, 100, 50)]
    [InlineData(ProviderType.Pooled, 49, 25)]
    [InlineData(ProviderType.BackupAndStats, 50, 45)]
    [InlineData(ProviderType.BackupAndStats, 49, 45)]
    [InlineData(ProviderType.BackupOnly, 50, 0)]
    [InlineData(ProviderType.Disabled, 50, 0)]
    public void DedicatedHealthProvidersKeepNinetyPercentOfTheirPoolWarm(
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
    public async Task HealthPrewarmFillsNinetyPercentOfDedicatedHealthProvidersOnly()
    {
        using var pooled = CreateProvider(ProviderType.Pooled, "pooled", 6, priority: 0);
        using var healthOnly = CreateProvider(ProviderType.HealthChecksOnly, "farm", 4, priority: 1);
        using var backupAndStats = CreateProvider(ProviderType.BackupAndStats, "block", 3, priority: 2);
        using var backupOnly = CreateProvider(ProviderType.BackupOnly, "backup", 5, priority: 3);
        using var client = new MultiProviderNntpClient(
            [pooled, healthOnly, backupAndStats, backupOnly], new ProviderUsageTracker());

        await client.PrewarmHealthCheckAsync(CancellationToken.None);

        Assert.Equal(0, pooled.LiveConnections);
        Assert.Equal(4, healthOnly.LiveConnections);
        Assert.Equal(3, backupAndStats.LiveConnections);
        Assert.Equal(0, backupOnly.LiveConnections);
    }

    [Fact]
    public async Task PrimaryHealthPrewarmRefillsPooledProvidersOnly()
    {
        using var pooled = CreateProvider(ProviderType.Pooled, "pooled", 10, priority: 0);
        using var backupAndStats = CreateProvider(ProviderType.BackupAndStats, "block", 10, priority: 1);
        using var backupOnly = CreateProvider(ProviderType.BackupOnly, "backup", 10, priority: 2);
        using var client = new MultiProviderNntpClient(
            [pooled, backupAndStats, backupOnly], new ProviderUsageTracker());

        await client.PrewarmPrimaryHealthCheckAsync(CancellationToken.None);

        Assert.Equal(9, pooled.LiveConnections);
        Assert.Equal(0, backupAndStats.LiveConnections);
        Assert.Equal(0, backupOnly.LiveConnections);
    }

    [Fact]
    public async Task StatPrimerExercisesOnlyTheRequestedHealthProviderRoles()
    {
        var pooledPrimed = 0;
        var healthPrimed = 0;
        var backupAndStatsPrimed = 0;
        var backupOnlyPrimed = 0;
        using var pooled = CreateProvider(
            ProviderType.Pooled, "pooled", 6, 0, () => Interlocked.Increment(ref pooledPrimed));
        using var healthOnly = CreateProvider(
            ProviderType.HealthChecksOnly, "farm", 4, 1, () => Interlocked.Increment(ref healthPrimed));
        using var backupAndStats = CreateProvider(
            ProviderType.BackupAndStats, "block", 3, 2,
            () => Interlocked.Increment(ref backupAndStatsPrimed));
        using var backupOnly = CreateProvider(
            ProviderType.BackupOnly, "backup", 5, 3,
            () => Interlocked.Increment(ref backupOnlyPrimed));
        using var client = new MultiProviderNntpClient(
            [pooled, healthOnly, backupAndStats, backupOnly], new ProviderUsageTracker());

        await client.PrewarmHealthCheckAsync(CancellationToken.None);
        await client.PrimeHealthCheckAsync(["one", "two"], 2, CancellationToken.None);

        Assert.Equal(0, pooledPrimed);
        Assert.Equal(4, healthPrimed);
        Assert.Equal(3, backupAndStatsPrimed);
        Assert.Equal(0, backupOnlyPrimed);

        await client.PrewarmPrimaryHealthCheckAsync(CancellationToken.None);
        await client.PrimePrimaryHealthCheckAsync(["one", "two"], 2, CancellationToken.None);

        Assert.Equal(6, pooledPrimed);
        Assert.Equal(4, healthPrimed);
        Assert.Equal(3, backupAndStatsPrimed);
        Assert.Equal(0, backupOnlyPrimed);
    }

    private static MultiConnectionNntpClient CreateProvider(
        ProviderType type,
        string host,
        int maxConnections,
        int priority,
        Action? onPrime = null)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections, _ => ValueTask.FromResult<INntpClient>(new StubNntpClient(onPrime)));
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

    private sealed class StubNntpClient(Action? onPrime = null) : NntpClient
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
        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            onPrime?.Invoke();
            foreach (var segmentId in segmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new PipelinedStatResult { SegmentId = segmentId, Exists = true };
            }
            await Task.CompletedTask;
        }
        public override void Dispose()
        {
        }
    }
}
