using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class HealthStatRecoveryTests
{
    [Fact]
    public async Task TransientBatchFailureRecoversInPlaceInsteadOfFailingTheHealthRun()
    {
        // Every provider attempt in the first pass stalls; the recovery pass
        // succeeds. The batch must complete without surfacing an exception.
        var transport = new ScriptedStatClient(
            (_, attempt) => attempt == 1 ? ScriptedBehavior.Stall : ScriptedBehavior.Succeed);
        using var client = new MultiProviderNntpClient([
                CreateProvider(transport, "flaky", maxConnections: 2),
            ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromMilliseconds(200));

        var results = await CollectAsync(
                client.StatsPipelinedAsync(["a", "b"], 2, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.True(result.Exists));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task RecoveryConcurrencyIsGloballyBoundedAcrossFailingBatches()
    {
        const int batches = 8;
        const int recoveryLimit = 4;
        var active = 0;
        var maxActive = 0;
        var recoveryLimitReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecoveries = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new ScriptedStatClient(
            (_, attempt) => attempt == 1 ? ScriptedBehavior.Stall : ScriptedBehavior.Succeed,
            onRecoveryAttempt: async () =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maxActive, current);
                if (current == recoveryLimit) recoveryLimitReached.TrySetResult();
                try
                {
                    await releaseRecoveries.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
        using var client = new MultiProviderNntpClient([
                CreateProvider(transport, "flaky", maxConnections: batches),
            ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(100),
            providerOperationTimeout: TimeSpan.FromSeconds(2));

        var lanes = Enumerable.Range(0, batches)
            .Select(index => CollectAsync(client.StatsPipelinedAsync(
                [$"segment-{index}"], 1, CancellationToken.None)))
            .ToArray();

        await recoveryLimitReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Give the remaining failed lanes a chance to (incorrectly) start their
        // recovery passes; the global gate must keep them queued instead.
        await Task.Delay(200);
        Assert.Equal(recoveryLimit, Volatile.Read(ref active));

        releaseRecoveries.TrySetResult();
        var results = await Task.WhenAll(lanes).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(recoveryLimit, Volatile.Read(ref maxActive));
        Assert.All(results, lane => Assert.All(
            lane, result => Assert.True(result.Exists)));
    }

    [Fact]
    public async Task FreshlyAcquiredSocketGetsAMeaningfulCommandWindow()
    {
        // The connection handshake consumes most of the nominal operation
        // budget; the STAT command itself is comfortably fast. Because the
        // command window starts only after acquisition, the batch succeeds
        // without needing a recovery pass.
        var transport = new ScriptedStatClient(
            (_, _) => ScriptedBehavior.Succeed,
            resultDelay: TimeSpan.FromMilliseconds(120));
        var pool = new ConnectionPool<INntpClient>(
            1,
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                return transport;
            });
        using var client = new MultiProviderNntpClient([
                CreateProvider(pool, "slow-handshake"),
            ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(100),
            providerOperationTimeout: TimeSpan.FromMilliseconds(250));

        var results = await CollectAsync(
                client.StatsPipelinedAsync(["a"], 1, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(Assert.Single(results).Exists);
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task PermanentProviderFailureFailsInBoundedTimeWithAnActionableError()
    {
        var transport = new ScriptedStatClient((_, _) => ScriptedBehavior.Stall);
        using var client = new MultiProviderNntpClient([
                CreateProvider(transport, "dead", maxConnections: 2),
            ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromMilliseconds(150));

        var timer = System.Diagnostics.Stopwatch.StartNew();
        // A batch that exhausts both the primary walk and the gated recovery
        // pass fails as provider unavailability — explicitly not as a missing
        // article — with the underlying timeout preserved for diagnostics.
        var exception = await Assert.ThrowsAsync<NzbWebDAV.Exceptions.UsenetArticleUnverifiableException>(
            () => CollectAsync(
                    client.StatsPipelinedAsync(["a"], 1, CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Contains("dead", exception.UnavailableProviders);
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    [Fact]
    public async Task CallerCancellationDuringARecoveryPassRemainsImmediate()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new ScriptedStatClient(
            (_, attempt) => attempt == 1 ? ScriptedBehavior.Stall : ScriptedBehavior.Succeed,
            onRecoveryAttempt: () =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            });
        using var client = new MultiProviderNntpClient([
                CreateProvider(transport, "flaky", maxConnections: 2),
            ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(
                client.StatsPipelinedAsync(["a"], 1, cancellation.Token))
            .WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task HundredsOfLanesWithLimitedLiveCapacityCompleteWithoutStalling()
    {
        var firstTransport = new ScriptedStatClient((_, _) => ScriptedBehavior.Succeed);
        var secondTransport = new ScriptedStatClient((_, _) => ScriptedBehavior.Succeed);
        using var client = new MultiProviderNntpClient([
                CreateProvider(firstTransport, "first", maxConnections: 4),
                CreateProvider(secondTransport, "second", maxConnections: 4),
            ],
            new ProviderUsageTracker());
        var segments = Enumerable.Range(0, 2048).Select(i => $"segment-{i}").ToArray();
        var progress = new MaximumProgress();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await client.CheckAllSegmentsPipelinedAsync(
            segments, depth: 32, fallbackConcurrency: 256, progress, timeout.Token);

        // Progress counts each verified segment exactly once, so a duplicated
        // partial result would overshoot the total.
        Assert.Equal(segments.Length, progress.Maximum);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    private static MultiConnectionNntpClient CreateProvider(
        INntpClient transport, string host, int maxConnections = 1)
        => CreateProvider(
            new ConnectionPool<INntpClient>(
                maxConnections, _ => ValueTask.FromResult(transport)),
            host);

    private static MultiConnectionNntpClient CreateProvider(
        ConnectionPool<INntpClient> pool, string host)
        => new(
            pool,
            ProviderType.Pooled,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority: 0,
            prepOnly: false,
            prepSpreadEnabled: true);

    private static async Task<List<PipelinedStatResult>> CollectAsync(
        IAsyncEnumerable<PipelinedStatResult> source)
    {
        var results = new List<PipelinedStatResult>();
        await foreach (var result in source) results.Add(result);
        return results;
    }

    private enum ScriptedBehavior
    {
        Succeed,
        Stall,
    }

    private sealed class MaximumProgress : IProgress<int>
    {
        private int _maximum;
        public int Maximum => Volatile.Read(ref _maximum);

        public void Report(int value)
        {
            var observed = Volatile.Read(ref _maximum);
            while (value > observed)
            {
                var previous = Interlocked.CompareExchange(ref _maximum, value, observed);
                if (previous == observed) return;
                observed = previous;
            }
        }
    }

    /// <summary>
    /// A pipelined STAT transport scripted per (global call number, per-batch
    /// attempt number). Batches are keyed by their first segment id, so a
    /// recovery attempt for a batch is distinguishable from its first attempt.
    /// </summary>
    private sealed class ScriptedStatClient(
        Func<int, int, ScriptedBehavior> script,
        Func<Task>? onRecoveryAttempt = null,
        TimeSpan? resultDelay = null) : NntpClient
    {
        private int _calls;
        private readonly ConcurrentDictionary<string, int> _batchAttempts = new();

        public int Calls => Volatile.Read(ref _calls);

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            var attempt = _batchAttempts.AddOrUpdate(
                segmentIds[0], 1, (_, previous) => previous + 1);
            if (attempt > 1 && onRecoveryAttempt is not null)
                await onRecoveryAttempt();

            if (script(call, attempt) == ScriptedBehavior.Stall)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            foreach (var segmentId in segmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (resultDelay is not null)
                    await Task.Delay(resultDelay.Value, cancellationToken);
                yield return new PipelinedStatResult { SegmentId = segmentId, Exists = true };
                await Task.Yield();
            }
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
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
