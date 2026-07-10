using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class PipelinedFallbackTests
{
    [Fact]
    public async Task MissingSegmentsFailOverAsOnePipelinedBatch()
    {
        var primaryClient = new RecordingPipelineClient([true, false, false]);
        var backupClient = new RecordingPipelineClient([true, true]);
        using var client = CreateClient(primaryClient, backupClient);

        var results = await CollectAsync(client.StatsPipelinedAsync(["a", "b", "c"], 3, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Exists));
        Assert.Equal(["a", "b", "c"], primaryClient.Batches.Single());
        Assert.Equal(["b", "c"], backupClient.Batches.Single());
        Assert.Equal(0, primaryClient.SequentialStatCalls);
        Assert.Equal(0, backupClient.SequentialStatCalls);
    }

    [Fact]
    public async Task FailedBatchRetriesOnlyUnreturnedSegmentsAsPipeline()
    {
        var primaryClient = new RecordingPipelineClient([true], failAfterResults: true);
        var backupClient = new RecordingPipelineClient([true, true]);
        using var client = CreateClient(primaryClient, backupClient);

        var results = await CollectAsync(client.StatsPipelinedAsync(["a", "b", "c"], 3, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Exists));
        Assert.Equal(["a", "b", "c"], primaryClient.Batches.Single());
        Assert.Equal(["b", "c"], backupClient.Batches.Single());
        Assert.Equal(0, primaryClient.SequentialStatCalls);
        Assert.Equal(0, backupClient.SequentialStatCalls);
    }

    [Fact]
    public async Task ConcurrentHealthLanesSpreadToBackupAndStatsCapacity()
    {
        var coordinator = new LaneCoordinator(2);
        var pooledClient = new CoordinatedPipelineClient(coordinator);
        var healthBackupClient = new CoordinatedPipelineClient(coordinator);
        using var client = new MultiProviderNntpClient([
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 0),
            CreateProvider(healthBackupClient, ProviderType.BackupAndStats, "health-backup", 1),
        ], new ProviderUsageTracker());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.CheckAllSegmentsPipelinedAsync(
            ["a", "b"], depth: 1, fallbackConcurrency: 2, progress: null, timeout.Token);

        Assert.Single(pooledClient.Batches);
        Assert.Single(healthBackupClient.Batches);
    }

    [Fact]
    public async Task DedicatedHealthProvidersKeepPooledProvidersOutOfPrimaryLanes()
    {
        var coordinator = new LaneCoordinator(2);
        var firstHealthClient = new CoordinatedPipelineClient(coordinator);
        var secondHealthClient = new CoordinatedPipelineClient(coordinator);
        var pooledClient = new RecordingPipelineClient([true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 0),
            CreateProvider(firstHealthClient, ProviderType.HealthChecksOnly, "health-1", 1),
            CreateProvider(secondHealthClient, ProviderType.HealthChecksOnly, "health-2", 2),
        ], new ProviderUsageTracker());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.CheckAllSegmentsPipelinedAsync(
            ["a", "b"], depth: 1, fallbackConcurrency: 2, progress: null, timeout.Token);

        Assert.Empty(pooledClient.Batches);
        Assert.Single(firstHealthClient.Batches);
        Assert.Single(secondHealthClient.Batches);
    }

    [Fact]
    public async Task DedicatedHealthMissesStillFallBackToPooledProvider()
    {
        var healthClient = new RecordingPipelineClient([false]);
        var pooledClient = new RecordingPipelineClient([true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 0),
            CreateProvider(healthClient, ProviderType.HealthChecksOnly, "health-only", 1),
        ], new ProviderUsageTracker());

        var results = await CollectAsync(
            client.StatsPipelinedAsync(["missing-on-health"], 8, CancellationToken.None));

        Assert.True(results.Single().Exists);
        Assert.Single(healthClient.Batches);
        Assert.Single(pooledClient.Batches);
    }

    [Fact]
    public async Task HealthChecksOnlyProviderServesStatsButNeverBodies()
    {
        var healthClient = new RecordingPipelineClient([true]);
        var pooledClient = new RecordingPipelineClient([true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(healthClient, ProviderType.HealthChecksOnly, "health-only", 0),
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 1),
        ], new ProviderUsageTracker());

        var stat = await client.StatAsync("sequential", CancellationToken.None);
        var pipelinedStats = await CollectAsync(
            client.StatsPipelinedAsync(["pipelined"], 8, CancellationToken.None));
        var body = await client.DecodedBodyAsync("body", CancellationToken.None);
        await body.Stream.DisposeAsync();
        await foreach (var pipelinedBody in client.DecodedBodiesPipelinedAsync(
                           ["pipelined-body"], 8, CancellationToken.None))
            if (pipelinedBody.Stream is not null) await pipelinedBody.Stream.DisposeAsync();

        Assert.True(stat.ArticleExists);
        Assert.True(pipelinedStats.Single().Exists);
        Assert.Equal(1, healthClient.SequentialStatCalls);
        Assert.Single(healthClient.Batches);
        Assert.Equal(0, healthClient.BodyCalls);
        Assert.Empty(healthClient.BodyDepths);
        Assert.Equal(0, pooledClient.SequentialStatCalls);
        Assert.Empty(pooledClient.Batches);
        Assert.Equal(1, pooledClient.BodyCalls);
        Assert.Single(pooledClient.BodyDepths);
    }

    [Fact]
    public async Task BulkHealthQualificationPinsWorkToProviderWithReleaseCoverage()
    {
        var missingClient = new CoveragePipelineClient(_ => false);
        var completeClient = new CoveragePipelineClient(_ => true);
        using var multiProvider = new MultiProviderNntpClient([
            CreateProvider(missingClient, ProviderType.Pooled, "missing", 0, maxConnections: 8),
            CreateProvider(completeClient, ProviderType.Pooled, "complete", 1, maxConnections: 8),
        ], new ProviderUsageTracker());
        using var wrapped = new WrappingNntpClient(multiProvider);
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();
        var progress = new MaximumProgress();

        await wrapped.CheckAllSegmentsPipelinedAsync(
            segments, depth: 8, fallbackConcurrency: 4, progress, CancellationToken.None);

        Assert.Single(missingClient.Batches);
        Assert.True(completeClient.Batches.Count > 1);
        Assert.Equal(segments.Length, progress.Maximum);
    }

    [Fact]
    public async Task BulkHealthQualificationDoesNotWaitForStalledProbe()
    {
        var stalledClient = new CoveragePipelineClient(
            _ => true,
            (batch, cancellationToken) => batch == 1
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : Task.CompletedTask);
        var completeClient = new CoveragePipelineClient(_ => true);
        using var multiProvider = new MultiProviderNntpClient([
            CreateProvider(stalledClient, ProviderType.Pooled, "stalled", 0, maxConnections: 8),
            CreateProvider(completeClient, ProviderType.Pooled, "complete", 1, maxConnections: 8),
        ], new ProviderUsageTracker());
        using var requestCts = new CancellationTokenSource();
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        await multiProvider.CheckAllSegmentsPipelinedAsync(
                segments, depth: 8, fallbackConcurrency: 4, progress: null, requestCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(stalledClient.ProbeCancelled);
        Assert.True(completeClient.Batches.Count > 1);
        await requestCts.CancelAsync();
        await stalledClient.ProbeCancellation.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HealthAndBodyPipelineDepthOverridesAreIndependent()
    {
        var transport = new RecordingPipelineClient([true, true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(transport, ProviderType.Pooled, "provider", 0,
                pipeliningDepth: 4, healthPipeliningDepth: 32),
        ], new ProviderUsageTracker());

        _ = await CollectAsync(client.StatsPipelinedAsync(["a", "b"], 8, CancellationToken.None));
        await foreach (var body in client.DecodedBodiesPipelinedAsync(["a", "b"], 8, CancellationToken.None))
            if (body.Stream is not null) await body.Stream.DisposeAsync();

        Assert.Equal(32, transport.StatDepths.Single());
        Assert.Equal(4, transport.BodyDepths.Single());
    }

    private static MultiProviderNntpClient CreateClient(params RecordingPipelineClient[] clients)
    {
        var providers = clients
            .Select((client, index) => CreateProvider(
                client, ProviderType.Pooled, $"provider-{index}", index))
            .ToList();
        return new MultiProviderNntpClient(providers, new ProviderUsageTracker());
    }

    private static MultiConnectionNntpClient CreateProvider(
        INntpClient client, ProviderType type, string host, int priority, int maxConnections = 1,
        int? pipeliningDepth = null, int? healthPipeliningDepth = null)
    {
        var pool = new ConnectionPool<INntpClient>(maxConnections, _ => ValueTask.FromResult(client));
        return new MultiConnectionNntpClient(
            pool,
            type,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority,
            prepOnly: false,
            prepSpreadEnabled: true,
            pipeliningDepth: pipeliningDepth,
            healthPipeliningDepth: healthPipeliningDepth);
    }

    private static async Task<List<PipelinedStatResult>> CollectAsync(
        IAsyncEnumerable<PipelinedStatResult> source)
    {
        var results = new List<PipelinedStatResult>();
        await foreach (var result in source) results.Add(result);
        return results;
    }

    private sealed class RecordingPipelineClient(
        IReadOnlyList<bool> results,
        bool failAfterResults = false) : NntpClient
    {
        public List<string[]> Batches { get; } = [];
        public List<int> StatDepths { get; } = [];
        public List<int> BodyDepths { get; } = [];
        public int SequentialStatCalls { get; private set; }
        public int BodyCalls { get; private set; }

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            SequentialStatCalls++;
            return Task.FromResult(new UsenetStatResponse
            {
                ResponseCode = (int)UsenetResponseType.ArticleExists,
                ResponseMessage = "223 article exists",
                ArticleExists = true,
            });
        }

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Batches.Add(segmentIds.ToArray());
            StatDepths.Add(depth);
            for (var i = 0; i < results.Count; i++)
            {
                yield return new PipelinedStatResult
                {
                    SegmentId = segmentIds[i],
                    Exists = results[i],
                };
                await Task.Yield();
            }

            if (failAfterResults)
                throw new IOException("Simulated batch failure.");
        }

        public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            BodyDepths.Add(depth);
            foreach (var segmentId in segmentIds)
            {
                yield return new PipelinedBodyResult
                {
                    SegmentId = segmentId,
                    Found = true,
                    Stream = new YencStream(new MemoryStream()),
                };
                await Task.Yield();
            }
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken cancellationToken)
        {
            BodyCalls++;
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new YencStream(new MemoryStream()),
            });
        }
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, Action<ArticleBodyResult>? callback, CancellationToken cancellationToken)
        {
            var response = DecodedBodyAsync(segmentId, cancellationToken);
            callback?.Invoke(ArticleBodyResult.Retrieved);
            return response;
        }
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

    private sealed class LaneCoordinator(int target)
    {
        private readonly TaskCompletionSource _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _started) == target) _ready.TrySetResult();
            await _ready.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CoordinatedPipelineClient(LaneCoordinator coordinator) : NntpClient
    {
        public List<string[]> Batches { get; } = [];

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Batches.Add(segmentIds.ToArray());
            await coordinator.ArriveAsync(cancellationToken);
            foreach (var segmentId in segmentIds)
                yield return new PipelinedStatResult { SegmentId = segmentId, Exists = true };
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

    private sealed class CoveragePipelineClient(
        Func<string, bool> exists,
        Func<int, CancellationToken, Task>? beforeBatch = null) : NntpClient
    {
        private int _batchCount;

        public ConcurrentQueue<string[]> Batches { get; } = [];
        public bool ProbeCancelled { get; private set; }
        public TaskCompletionSource ProbeCancellation { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Batches.Enqueue(segmentIds.ToArray());
            var batch = Interlocked.Increment(ref _batchCount);
            try
            {
                if (beforeBatch is not null) await beforeBatch(batch, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ProbeCancelled = true;
                ProbeCancellation.TrySetResult();
                throw;
            }

            foreach (var segmentId in segmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new PipelinedStatResult { SegmentId = segmentId, Exists = exists(segmentId) };
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
}
