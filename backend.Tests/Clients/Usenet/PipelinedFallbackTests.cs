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
    public async Task StalledBatchTimesOutAndRetriesOnNextProvider()
    {
        var stalledClient = new CoveragePipelineClient(
            _ => true,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var backupClient = new RecordingPipelineClient([true, true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(stalledClient, ProviderType.Pooled, "stalled", 0),
            CreateProvider(backupClient, ProviderType.Pooled, "backup", 1),
        ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromSeconds(1));

        var results = await CollectAsync(
            client.StatsPipelinedAsync(["a", "b"], 2, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Exists));
        Assert.Single(stalledClient.Batches);
        Assert.Equal(["a", "b"], backupClient.Batches.Single());
    }

    [Fact]
    public async Task TimedOutBatchRetriesOnlyResponsesThatWereNotResolved()
    {
        var stalledClient = new RecordingPipelineClient(
            [true], stallAfterResults: true);
        var backupClient = new RecordingPipelineClient([true, true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(stalledClient, ProviderType.Pooled, "stalled", 0),
            CreateProvider(backupClient, ProviderType.Pooled, "backup", 1),
        ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromSeconds(1));

        var results = await CollectAsync(
            client.StatsPipelinedAsync(["a", "b", "c"], 3, CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Exists));
        Assert.Equal(["b", "c"], backupClient.Batches.Single());
    }

    [Fact]
    public async Task StalledFinalProviderCannotHoldPipelinedHealthForever()
    {
        var stalledClient = new CoveragePipelineClient(
            _ => true,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        using var client = new MultiProviderNntpClient([
            CreateProvider(stalledClient, ProviderType.Pooled, "stalled", 0),
        ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(25),
            providerOperationTimeout: TimeSpan.FromMilliseconds(75));

        await Assert.ThrowsAsync<TimeoutException>(() => CollectAsync(
            client.StatsPipelinedAsync(["a"], 1, CancellationToken.None)));
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToPipelinedProviderTimeout()
    {
        var stalledClient = new CoveragePipelineClient(
            _ => true,
            (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var backupClient = new RecordingPipelineClient([true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(stalledClient, ProviderType.Pooled, "stalled", 0),
            CreateProvider(backupClient, ProviderType.Pooled, "backup", 1),
        ],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromSeconds(1),
            providerOperationTimeout: TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectAsync(
            client.StatsPipelinedAsync(["a"], 1, cancellation.Token)));

        Assert.Empty(backupClient.Batches);
    }

    [Fact]
    public async Task FailedBodyBatchRotatesTheNextChunkToAnotherProvider()
    {
        var primaryClient = new RecordingPipelineClient([], failBodyAfterResults: 1);
        var backupClient = new RecordingPipelineClient([]);
        using var client = CreateClient(primaryClient, backupClient);

        await DisposeBodiesAsync(client.DecodedBodiesPipelinedAsync(
            ["a", "b", "c", "d"], 2, CancellationToken.None));

        Assert.Equal(["a", "b"], primaryClient.BodyBatches.Single());
        Assert.Equal(["c", "d"], backupClient.BodyBatches.Single());
    }

    [Fact]
    public async Task CompletedBodyBatchReturnsItsConnectionToThePool()
    {
        var transport = new RecordingPipelineClient([]);
        var provider = CreateProvider(transport, ProviderType.Pooled, "complete", 0);
        using var client = new MultiProviderNntpClient([provider], new ProviderUsageTracker());

        await DisposeBodiesAsync(client.DecodedBodiesPipelinedAsync(
            ["a", "b"], 2, CancellationToken.None));

        Assert.Equal(1, provider.LiveConnections);
        Assert.Equal(1, provider.IdleConnections);
    }

    [Fact]
    public async Task RepeatedPartialBatchFailuresTripProviderCircuitBreaker()
    {
        var transport = new RecordingPipelineClient([true], failAfterResults: true);
        using var provider = CreateProvider(transport, ProviderType.Pooled, "partial", 0);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<IOException>(() => CollectAsync(
                provider.StatsPipelinedAsync(["a", "b"], 2, CancellationToken.None)));
        }

        Assert.True(provider.IsTripped);
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
    public async Task NormalHealthSkipsColdProviderUntilPrewarmMakesItLive()
    {
        var coldFactoryCalls = 0;
        var releaseColdFactory = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var coldTransport = new CoveragePipelineClient(_ => true);
        var coldPool = new ConnectionPool<INntpClient>(
            50,
            async cancellationToken =>
            {
                Interlocked.Increment(ref coldFactoryCalls);
                await releaseColdFactory.Task.WaitAsync(cancellationToken);
                return coldTransport;
            });
        var cold = CreateProvider(coldPool, ProviderType.Pooled, "cold", 0);

        var readyTransport = new CoveragePipelineClient(_ => true);
        var readyPool = new ConnectionPool<INntpClient>(
            4, _ => ValueTask.FromResult<INntpClient>(readyTransport));
        await readyPool.PrewarmAsync(4);
        var ready = CreateProvider(readyPool, ProviderType.Pooled, "ready", 1);
        using var client = new MultiProviderNntpClient(
            [cold, ready], new ProviderUsageTracker());

        var readyResult = await CollectAsync(
            client.StatsPipelinedAsync(["ready-segment"], 1, CancellationToken.None));

        Assert.True(readyResult.Single().Exists);
        Assert.Equal(0, Volatile.Read(ref coldFactoryCalls));
        Assert.Single(readyTransport.Batches);

        releaseColdFactory.TrySetResult();
        await cold.PrewarmAsync(1, CancellationToken.None);
        var recoveredResult = await CollectAsync(
            client.StatsPipelinedAsync(["cold-segment"], 1, CancellationToken.None));

        Assert.True(recoveredResult.Single().Exists);
        Assert.Equal(1, Volatile.Read(ref coldFactoryCalls));
        Assert.Single(coldTransport.Batches);
    }

    [Fact]
    public async Task NormalHealthDistributesLanesByEstablishedLiveCapacity()
    {
        var coordinator = new LaneCoordinator(5);
        var sparseTransport = new CoordinatedPipelineClient(coordinator);
        var sparseFactoryCalls = 0;
        var sparsePool = new ConnectionPool<INntpClient>(
            50,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref sparseFactoryCalls) > 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return sparseTransport;
            });
        await sparsePool.PrewarmAsync(1);
        var sparse = CreateProvider(sparsePool, ProviderType.Pooled, "sparse", 0);

        var denseTransport = new CoordinatedPipelineClient(coordinator);
        var densePool = new ConnectionPool<INntpClient>(
            4, _ => ValueTask.FromResult<INntpClient>(denseTransport));
        await densePool.PrewarmAsync(4);
        var dense = CreateProvider(densePool, ProviderType.Pooled, "dense", 1);
        using var client = new MultiProviderNntpClient(
            [sparse, dense],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.CheckAllSegmentsPipelinedAsync(
            ["a", "b", "c", "d", "e"],
            depth: 1,
            fallbackConcurrency: 5,
            progress: null,
            timeout.Token);

        Assert.Equal(1, Volatile.Read(ref sparseFactoryCalls));
        Assert.Single(sparseTransport.Batches);
        Assert.Equal(4, denseTransport.Batches.Count);
    }

    [Fact]
    public async Task DedicatedAndPooledProvidersSharePrimaryHealthLanes()
    {
        var coordinator = new LaneCoordinator(2);
        var firstHealthClient = new CoordinatedPipelineClient(coordinator);
        var secondHealthClient = new CoordinatedPipelineClient(coordinator);
        var pooledClient = new CoordinatedPipelineClient(coordinator);
        using var client = new MultiProviderNntpClient([
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 0),
            CreateProvider(firstHealthClient, ProviderType.HealthChecksOnly, "health-1", 1),
            CreateProvider(secondHealthClient, ProviderType.HealthChecksOnly, "health-2", 2),
        ], new ProviderUsageTracker());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.CheckAllSegmentsPipelinedAsync(
            ["a", "b"], depth: 1, fallbackConcurrency: 2, progress: null, timeout.Token);

        Assert.Single(pooledClient.Batches);
        Assert.Single(firstHealthClient.Batches);
        Assert.Empty(secondHealthClient.Batches);
    }

    [Fact]
    public async Task BackupAndStatsProvidersJoinPrimaryHealthLanes()
    {
        var coordinator = new LaneCoordinator(2);
        var pooledClient = new CoordinatedPipelineClient(coordinator);
        var backupClient = new CoordinatedPipelineClient(coordinator);
        using var client = new MultiProviderNntpClient([
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 0),
            CreateProvider(backupClient, ProviderType.BackupAndStats, "backup-health", 1),
        ], new ProviderUsageTracker());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await client.CheckAllSegmentsPipelinedAsync(
            ["a", "b"], depth: 1, fallbackConcurrency: 2, progress: null, timeout.Token);

        Assert.Single(pooledClient.Batches);
        Assert.Single(backupClient.Batches);
    }

    [Fact]
    public async Task BackupOnlyProvidersDoNotJoinPrimaryHealthLanes()
    {
        var pooledClient = new RecordingPipelineClient([true]);
        var backupClient = new RecordingPipelineClient([true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 0),
            CreateProvider(backupClient, ProviderType.BackupOnly, "backup", 1),
        ], new ProviderUsageTracker());

        var results = await CollectAsync(
            client.StatsPipelinedAsync(["article"], 8, CancellationToken.None));

        Assert.True(results.Single().Exists);
        Assert.Single(pooledClient.Batches);
        Assert.Empty(backupClient.Batches);
    }

    [Fact]
    public async Task DedicatedHealthMissesStillFallBackToPooledProvider()
    {
        var healthClient = new RecordingPipelineClient([false]);
        var pooledClient = new RecordingPipelineClient([true]);
        using var client = new MultiProviderNntpClient([
            CreateProvider(pooledClient, ProviderType.Pooled, "pooled", 1),
            CreateProvider(healthClient, ProviderType.HealthChecksOnly, "health-only", 0),
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
    public async Task BulkPlanDoesNotAssignPrimaryLanesToPreferredProviderThatWentCold()
    {
        static bool PartialCoverage(string segmentId) =>
            int.Parse(segmentId.AsSpan("segment-".Length)) % 4 != 0;

        var coldFactoryCalls = 0;
        var coldTransport = new CoveragePipelineClient(PartialCoverage);
        var coldPool = new ConnectionPool<INntpClient>(
            50,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref coldFactoryCalls) > 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return coldTransport;
            });
        var cold = CreateProvider(coldPool, ProviderType.Pooled, "cold", 0);

        var releaseReadyProbe = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readyTransport = new CoveragePipelineClient(
            _ => true,
            (batch, cancellationToken) => batch == 1
                ? releaseReadyProbe.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask);
        var readyPool = new ConnectionPool<INntpClient>(
            4, _ => ValueTask.FromResult<INntpClient>(readyTransport));
        await readyPool.PrewarmAsync(4);
        var ready = CreateProvider(readyPool, ProviderType.Pooled, "ready", 1);
        using var client = new MultiProviderNntpClient(
            [cold, ready],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromSeconds(1));
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        var check = client.CheckAllSegmentsPipelinedAsync(
            segments, depth: 8, fallbackConcurrency: 4,
            progress: null, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (cold.IdleConnections == 0)
            await Task.Delay(10, timeout.Token);

        cold.CloseIdleConnections();
        Assert.Equal(0, cold.LiveConnections);
        releaseReadyProbe.TrySetResult();
        await check.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref coldFactoryCalls));
        Assert.Single(coldTransport.Batches);
        Assert.True(readyTransport.Batches.Count > 1);
    }

    [Fact]
    public async Task BulkHealthWaitsForLateCoverageUntilPreferredCapacityCanFillLanes()
    {
        var lateProbeCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bulkStartedTooSoon = 0;
        var firstClient = new CoveragePipelineClient(
            _ => true,
            (batch, _) =>
            {
                if (batch > 1 && !lateProbeCompleted.Task.IsCompleted)
                    Volatile.Write(ref bulkStartedTooSoon, 1);
                return Task.CompletedTask;
            });
        var lateClient = new CoveragePipelineClient(
            _ => true,
            async (batch, cancellationToken) =>
            {
                if (batch != 1) return;
                await Task.Delay(200, cancellationToken);
                lateProbeCompleted.TrySetResult();
            });
        using var multiProvider = new MultiProviderNntpClient([
            CreateProvider(firstClient, ProviderType.Pooled, "first", 0, maxConnections: 2),
            CreateProvider(lateClient, ProviderType.Pooled, "late", 1, maxConnections: 6),
        ], new ProviderUsageTracker(), applicationConnectionLimit: 8);
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        await multiProvider.CheckAllSegmentsPipelinedAsync(
            segments, depth: 8, fallbackConcurrency: 8, progress: null, CancellationToken.None);

        Assert.True(lateProbeCompleted.Task.IsCompletedSuccessfully);
        Assert.Equal(0, Volatile.Read(ref bulkStartedTooSoon));
    }

    [Fact]
    public async Task BulkHealthReclaimsIdleSocketsFromNonContributorsAndRestoresThemAfterward()
    {
        var bulkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBulk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var missingClient = new CoveragePipelineClient(_ => false);
        var completeClient = new CoveragePipelineClient(
            _ => true,
            async (batch, cancellationToken) =>
            {
                if (batch <= 1) return;
                bulkStarted.TrySetResult();
                await releaseBulk.Task.WaitAsync(cancellationToken);
            });
        using var missing = CreateProvider(
            missingClient, ProviderType.HealthChecksOnly, "missing", 0, maxConnections: 8);
        using var complete = CreateProvider(
            completeClient, ProviderType.HealthChecksOnly, "complete", 1, maxConnections: 8);
        using var multiProvider = new MultiProviderNntpClient(
            [missing, complete], new ProviderUsageTracker(), applicationConnectionLimit: 12);
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        await missing.PrewarmAsync(8, CancellationToken.None);
        await complete.PrewarmAsync(4, CancellationToken.None);
        var check = multiProvider.CheckAllSegmentsPipelinedAsync(
            segments, depth: 8, fallbackConcurrency: 16, progress: null, CancellationToken.None);

        await bulkStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(4, missing.LiveConnections);
        Assert.Equal(4, missing.IdleConnections);

        releaseBulk.TrySetResult();
        await check.WaitAsync(TimeSpan.FromSeconds(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (missing.LiveConnections < 8)
            await Task.Delay(10, timeout.Token);

        Assert.Equal(8, missing.IdleConnections);
    }

    [Fact]
    public async Task BulkHealthSummaryIncludesUnprobedBackupThatRescuesAMiss()
    {
        static bool PrimaryCoverage(string segmentId) => segmentId != "segment-318";

        var firstPrimary = new CoveragePipelineClient(PrimaryCoverage);
        var secondPrimary = new CoveragePipelineClient(PrimaryCoverage);
        var backup = new CoveragePipelineClient(_ => true);
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using var multiProvider = new MultiProviderNntpClient([
            CreateProvider(firstPrimary, ProviderType.Pooled, "primary-1", 0, maxConnections: 8),
            CreateProvider(secondPrimary, ProviderType.Pooled, "primary-2", 1, maxConnections: 8),
            CreateProvider(backup, ProviderType.BackupOnly, "rescue", 2, maxConnections: 1),
        ], tracker);
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        using (tracker.BeginScope(queueId))
            await multiProvider.CheckAllSegmentsPipelinedAsync(
                segments, depth: 8, fallbackConcurrency: 12, progress: null, CancellationToken.None);

        var snapshot = Assert.IsType<HealthCheckUsageSnapshot>(tracker.SnapshotHealthCheck(queueId));
        var rescue = Assert.Single(snapshot.Providers, provider => provider.Host == "rescue");
        Assert.Equal(segments.Length, snapshot.FoundArticles);
        Assert.Equal(0, snapshot.MissingArticles);
        Assert.False(rescue.Preferred);
        Assert.Equal(0, rescue.ProbeReceived);
        Assert.Equal(1, rescue.Attempted);
        Assert.Equal(1, rescue.Found);
        Assert.Equal(0, rescue.Missing);
    }

    [Fact]
    public async Task BulkHealthKeepsFourGlobalSlotsAvailableForBackupOnlyRecovery()
    {
        static bool PrimaryCoverage(string segmentId) => segmentId != "segment-318";

        var budget = new ConnectionLifetimeBudget(12, 12);
        var firstPrimaryClient = new CoveragePipelineClient(PrimaryCoverage);
        var secondPrimaryClient = new CoveragePipelineClient(PrimaryCoverage);
        var backupClient = new CoveragePipelineClient(_ => true);
        using var firstPrimary = CreateProvider(
            firstPrimaryClient, ProviderType.Pooled, "primary-1", 0,
            maxConnections: 8, connectionBudget: budget);
        using var secondPrimary = CreateProvider(
            secondPrimaryClient, ProviderType.Pooled, "primary-2", 1,
            maxConnections: 4, connectionBudget: budget);
        using var backup = CreateProvider(
            backupClient, ProviderType.BackupOnly, "rescue", 2,
            maxConnections: 4, connectionBudget: budget);
        using var multiProvider = new MultiProviderNntpClient(
            [firstPrimary, secondPrimary, backup],
            new ProviderUsageTracker(),
            applicationConnectionLimit: 12,
            connectionBudget: budget);
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        await firstPrimary.PrewarmAsync(8, CancellationToken.None);
        await secondPrimary.PrewarmAsync(4, CancellationToken.None);
        await multiProvider.CheckAllSegmentsPipelinedAsync(
                segments, depth: 8, fallbackConcurrency: 12,
                progress: null, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(
            backupClient.Batches.SelectMany(batch => batch),
            segment => segment == "segment-318");
        Assert.Equal(0, backup.LiveConnections);
        Assert.Equal(8, budget.ReservedConnections);
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

        var timer = System.Diagnostics.Stopwatch.StartNew();
        await multiProvider.CheckAllSegmentsPipelinedAsync(
                segments, depth: 8, fallbackConcurrency: 12, progress: null, requestCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(stalledClient.ProbeCancelled);
        Assert.True(completeClient.Batches.Count > 1);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1.5));
        await requestCts.CancelAsync();
        await stalledClient.ProbeCancellation.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task BulkHealthRecoversPrepTrippedProviderWithControlledStatProbe()
    {
        var recoveredClient = new CoveragePipelineClient(_ => true);
        var healthyClient = new CoveragePipelineClient(
            _ => true,
            (_, cancellationToken) => Task.Delay(20, cancellationToken));
        var breaker = new ProviderCircuitBreaker("prep-tripped");
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordFailure();
        var recoveredProvider = CreateProvider(
            recoveredClient, ProviderType.Pooled, "prep-tripped", 0,
            maxConnections: 8, circuitBreaker: breaker);
        using var multiProvider = new MultiProviderNntpClient([
            recoveredProvider,
            CreateProvider(healthyClient, ProviderType.Pooled, "healthy", 1, maxConnections: 8),
        ], new ProviderUsageTracker());
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        await multiProvider.CheckAllSegmentsPipelinedAsync(
            segments, depth: 8, fallbackConcurrency: 8, progress: null, CancellationToken.None);

        Assert.False(recoveredProvider.IsTripped);
        Assert.True(recoveredClient.Batches.Count > 1);
    }

    [Fact]
    public async Task EconomicallyUsefulSeventyFivePercentProvidersJoinBulkHealth()
    {
        static bool FarmCoverage(string segmentId) =>
            int.Parse(segmentId.AsSpan("segment-".Length)) % 4 != 0;

        var firstFarm = new CoveragePipelineClient(FarmCoverage);
        var secondFarm = new CoveragePipelineClient(FarmCoverage);
        var complete = new CoveragePipelineClient(
            _ => true,
            (_, cancellationToken) => Task.Delay(20, cancellationToken));
        using var multiProvider = new MultiProviderNntpClient([
            CreateProvider(firstFarm, ProviderType.HealthChecksOnly, "farm-1", 0, maxConnections: 8),
            CreateProvider(secondFarm, ProviderType.HealthChecksOnly, "farm-2", 1, maxConnections: 8),
            CreateProvider(complete, ProviderType.Pooled, "complete", 2, maxConnections: 8),
        ], new ProviderUsageTracker());
        var segments = Enumerable.Range(0, 320).Select(x => $"segment-{x}").ToArray();

        await multiProvider.CheckAllSegmentsPipelinedAsync(
            segments, depth: 8, fallbackConcurrency: 12, progress: null, CancellationToken.None);

        Assert.True(firstFarm.Batches.Count > 1);
        Assert.True(secondFarm.Batches.Count > 1);

        // After qualification, partial peers receive disjoint primary work.
        // A miss on one goes directly to the complete provider rather than
        // repeating through the other account with identical coverage.
        var firstFarmBulkSegments = firstFarm.Batches.Skip(1).SelectMany(x => x).ToHashSet();
        var secondFarmBulkSegments = secondFarm.Batches.Skip(1).SelectMany(x => x).ToHashSet();
        Assert.Empty(firstFarmBulkSegments.Intersect(secondFarmBulkSegments));
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
        int? pipeliningDepth = null, int? healthPipeliningDepth = null,
        ProviderCircuitBreaker? circuitBreaker = null,
        ConnectionLifetimeBudget? connectionBudget = null)
    {
        var pool = new ConnectionPool<INntpClient>(
            maxConnections,
            _ => ValueTask.FromResult(client),
            connectionBudget: connectionBudget,
            useRecoveryCapacity: type == ProviderType.BackupOnly);
        return new MultiConnectionNntpClient(
            pool,
            type,
            circuitBreaker ?? new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority,
            prepOnly: false,
            prepSpreadEnabled: true,
            pipeliningDepth: pipeliningDepth,
            healthPipeliningDepth: healthPipeliningDepth);
    }

    private static MultiConnectionNntpClient CreateProvider(
        ConnectionPool<INntpClient> pool,
        ProviderType type,
        string host,
        int priority) =>
        new(
            pool,
            type,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority,
            prepOnly: false,
            prepSpreadEnabled: true);

    private static async Task<List<PipelinedStatResult>> CollectAsync(
        IAsyncEnumerable<PipelinedStatResult> source)
    {
        var results = new List<PipelinedStatResult>();
        await foreach (var result in source) results.Add(result);
        return results;
    }

    private static async Task DisposeBodiesAsync(IAsyncEnumerable<PipelinedBodyResult> source)
    {
        await foreach (var result in source)
            if (result.Stream is not null)
                await result.Stream.DisposeAsync();
    }

    private sealed class RecordingPipelineClient(
        IReadOnlyList<bool> results,
        bool failAfterResults = false,
        int? failBodyAfterResults = null,
        bool stallAfterResults = false) : NntpClient
    {
        public List<string[]> Batches { get; } = [];
        public List<string[]> BodyBatches { get; } = [];
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

            if (stallAfterResults)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            if (failAfterResults)
                throw new IOException("Simulated batch failure.");
        }

        public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            BodyBatches.Add(segmentIds.ToArray());
            BodyDepths.Add(depth);
            for (var index = 0; index < segmentIds.Count; index++)
            {
                if (index == failBodyAfterResults)
                    throw new IOException("Simulated BODY batch failure.");

                yield return new PipelinedBodyResult
                {
                    SegmentId = segmentIds[index],
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
