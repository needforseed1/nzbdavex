using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
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

    [Fact]
    public async Task PrepSpreadFlagKeepsProviderOutOfFirstChoicePool()
    {
        var reservedTransport = new BodyClient();
        await using var reservedPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(reservedTransport));
        await reservedPool.PrewarmAsync(1);
        using var reserved = CreateProvider(
            reservedPool, "reserved", priority: 0, prepSpreadEnabled: false);

        var spreadTransport = new BodyClient();
        await using var spreadPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(spreadTransport));
        await spreadPool.PrewarmAsync(1);
        using var spread = CreateProvider(
            spreadPool, "spread", priority: 1, prepSpreadEnabled: true);
        using var client = new MultiProviderNntpClient([reserved, spread], new ProviderUsageTracker());

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream.DisposeAsync();

        Assert.Equal(0, reservedTransport.BodyCalls);
        Assert.Equal(1, spreadTransport.BodyCalls);
    }

    [Fact]
    public async Task PrepUsesReadyBackupInsteadOfWaitingForColdPrimary()
    {
        var coldFactoryCalls = 0;
        var coldRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coldPool = new ConnectionPool<INntpClient>(
            1,
            async ct =>
            {
                Interlocked.Increment(ref coldFactoryCalls);
                await coldRelease.Task.WaitAsync(ct);
                return new BodyClient();
            });
        using var coldPrimary = CreateProvider(coldPool, "cold-primary", 0);

        var backupTransport = new BodyClient();
        await using var backupPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(backupTransport));
        await backupPool.PrewarmAsync(1);
        using var backup = CreateProvider(
            backupPool, "ready-backup", 1, type: ProviderType.BackupOnly);
        using var client = new MultiProviderNntpClient(
            [coldPrimary, backup], new ProviderUsageTracker());

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream.DisposeAsync();

        Assert.Equal(0, Volatile.Read(ref coldFactoryCalls));
        Assert.Equal(1, backupTransport.BodyCalls);
    }

    [Fact]
    public async Task PrepFallsBackToBackupAfterPrimaryArticleMiss()
    {
        var primaryTransport = new BodyClient(articleFound: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var backupTransport = new BodyClient();
        await using var backupPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(backupTransport));
        await backupPool.PrewarmAsync(1);
        using var backup = CreateProvider(
            backupPool, "backup", 1, type: ProviderType.BackupOnly);
        using var client = new MultiProviderNntpClient(
            [primary, backup], new ProviderUsageTracker());

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await response.Stream.DisposeAsync();

        Assert.Equal(1, primaryTransport.BodyCalls);
        Assert.Equal(1, backupTransport.BodyCalls);
    }

    [Fact]
    public async Task PrepMovesToBackupWhenPrimaryAttemptStalls()
    {
        var primaryTransport = new BodyClient(stall: true);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var backupTransport = new BodyClient();
        await using var backupPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(backupTransport));
        await backupPool.PrewarmAsync(1);
        using var backup = CreateProvider(
            backupPool, "backup", 1, type: ProviderType.BackupOnly);
        using var client = new MultiProviderNntpClient(
            [primary, backup],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromSeconds(1));

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await response.Stream.DisposeAsync();

        Assert.Equal(1, primaryTransport.BodyCalls);
        Assert.Equal(1, backupTransport.BodyCalls);
    }

    [Fact]
    public async Task PrepReturnsRetryableFailureWhenEveryProviderStalls()
    {
        var transport = new BodyClient(stall: true);
        await using var pool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(transport));
        await pool.PrewarmAsync(1);
        using var provider = CreateProvider(pool, "stalled", 0);
        using var client = new MultiProviderNntpClient(
            [provider],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(() =>
            client.DecodedBodyAsync("segment", CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToProviderFailure()
    {
        var transport = new BodyClient(stall: true);
        await using var pool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(transport));
        await pool.PrewarmAsync(1);
        using var provider = CreateProvider(pool, "stalled", 0);
        using var client = new MultiProviderNntpClient(
            [provider],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromSeconds(1),
            providerOperationTimeout: TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.DecodedBodyAsync("segment", cancellation.Token));
    }

    [Fact]
    public async Task CallerCancellationRemainsLinkedWhileReturnedBodyIsOpen()
    {
        var transport = new BodyClient();
        await using var pool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(transport));
        await pool.PrewarmAsync(1);
        using var provider = CreateProvider(pool, "provider", 0);
        using var client = new MultiProviderNntpClient([provider], new ProviderUsageTracker());
        using var cancellation = new CancellationTokenSource();

        var response = await client.DecodedBodyAsync("segment", cancellation.Token);
        Assert.False(transport.LastCancellationToken.IsCancellationRequested);

        cancellation.Cancel();

        Assert.True(transport.LastCancellationToken.IsCancellationRequested);
        await response.Stream.DisposeAsync();
    }

    [Fact]
    public async Task ProviderStartupDeadlineStopsAfterBodyStreamingBegins()
    {
        var transport = new BodyClient();
        await using var pool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(transport));
        await pool.PrewarmAsync(1);
        using var provider = CreateProvider(pool, "provider", 0);
        using var client = new MultiProviderNntpClient(
            [provider],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(25),
            providerOperationTimeout: TimeSpan.FromMilliseconds(50));

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        await Task.Delay(100);

        Assert.False(transport.LastCancellationToken.IsCancellationRequested);
        await response.Stream.DisposeAsync();
    }

    private static MultiConnectionNntpClient CreateProvider(
        ConnectionPool<INntpClient> pool,
        string host,
        int priority,
        bool prepSpreadEnabled = true,
        ProviderType type = ProviderType.Pooled) =>
        new(
            pool,
            type,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority,
            prepOnly: false,
            prepSpreadEnabled);

    private sealed class BodyClient(bool articleFound = true, bool stall = false) : NntpClient
    {
        private int _bodyCalls;
        public int BodyCalls => Volatile.Read(ref _bodyCalls);
        public CancellationToken LastCancellationToken { get; private set; }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _bodyCalls);
            LastCancellationToken = cancellationToken;
            if (stall)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (!articleFound)
                throw new UsenetArticleNotFoundException(segmentId);
            callback?.Invoke(ArticleBodyResult.Retrieved);
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new YencStream(new MemoryStream()),
            };
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
