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

    private static MultiProviderNntpClient CreateClient(params RecordingPipelineClient[] clients)
    {
        var providers = clients
            .Select((client, index) => CreateProvider(
                client, ProviderType.Pooled, $"provider-{index}", index))
            .ToList();
        return new MultiProviderNntpClient(providers, new ProviderUsageTracker());
    }

    private static MultiConnectionNntpClient CreateProvider(
        INntpClient client, ProviderType type, string host, int priority)
    {
        var pool = new ConnectionPool<INntpClient>(1, _ => ValueTask.FromResult(client));
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
        public int SequentialStatCalls { get; private set; }

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            SequentialStatCalls++;
            throw new InvalidOperationException("STAT fallback must remain pipelined.");
        }

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Batches.Add(segmentIds.ToArray());
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

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
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
}
