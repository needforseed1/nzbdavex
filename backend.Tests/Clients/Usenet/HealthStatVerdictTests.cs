using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

/// <summary>
/// Three-state STAT verdicts: found / confirmed missing / indeterminate.
/// A segment is confirmed missing only when every eligible provider answered
/// "missing"; a silent provider (timed out, quarantined, gate-busy backup)
/// makes the verdict indeterminate, resolved by the coordinated recovery pass
/// or reported as unverifiable — never as a missing article.
/// </summary>
public class HealthStatVerdictTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task MidChainProviderTimeoutWithLaterMissIsUnverifiableNotMissing()
    {
        var stalling = new VerdictStatClient((_, _) => StatAnswer.Stall);
        var missing = new VerdictStatClient((_, _) => StatAnswer.Missing);
        using var client = CreateClient(
            [Provider(stalling, "stalling"), Provider(missing, "missing")]);

        var exception = await Assert.ThrowsAsync<UsenetArticleUnverifiableException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    ["s1", "s2"], depth: 2, fallbackConcurrency: 2, null, CancellationToken.None)
                .WaitAsync(TestTimeout));

        Assert.Contains("stalling", exception.UnavailableProviders);
    }

    [Fact]
    public async Task AllEligibleProvidersAnsweringMissingConfirmsMissing()
    {
        var first = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var second = new VerdictStatClient((_, _) => StatAnswer.Missing);
        using var client = CreateClient(
            [Provider(first, "first"), Provider(second, "second")]);

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    ["s1", "s2"], depth: 2, fallbackConcurrency: 2, null, CancellationToken.None)
                .WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task TimedOutProviderRecoveringInCoordinatorCompletesTheRun()
    {
        // The flaky provider stalls during lane work but answers when the
        // coordinated recovery pass consults it — and it finds the articles the
        // other provider missed. The run must complete without any exception.
        var flaky = new VerdictStatClient(
            (_, call) => call <= 2 ? StatAnswer.Stall : StatAnswer.Found);
        var missing = new VerdictStatClient((_, _) => StatAnswer.Missing);
        using var client = CreateClient(
            [Provider(flaky, "flaky"), Provider(missing, "missing")]);

        await client.CheckAllSegmentsPipelinedAsync(
                ["s1", "s2"], depth: 2, fallbackConcurrency: 2, null, CancellationToken.None)
            .WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task UnrecoverableProviderYieldsUnverifiableWithProviderAttribution()
    {
        var dead = new VerdictStatClient((_, _) => StatAnswer.Stall);
        var missing = new VerdictStatClient((_, _) => StatAnswer.Missing);
        using var client = CreateClient(
            [Provider(dead, "dead"), Provider(missing, "missing")]);

        var exception = await Assert.ThrowsAsync<UsenetArticleUnverifiableException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    ["s1", "s2"], depth: 2, fallbackConcurrency: 2, null, CancellationToken.None)
                .WaitAsync(TestTimeout));

        Assert.Equal(["s1", "s2"], exception.SegmentIds.OrderBy(x => x));
        Assert.Contains("dead", exception.UnavailableProviders);
    }

    [Fact]
    public async Task CallerCancellationDuringCoordinatedRecoveryRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var stalling = new VerdictStatClient((_, call) =>
        {
            // Calls 1-2 are the two lane batches; call 3 is the coordinated
            // recovery attempt: cancel the caller while it is in flight.
            if (call >= 3) cancellation.Cancel();
            return StatAnswer.Stall;
        });
        var missing = new VerdictStatClient((_, _) => StatAnswer.Missing);
        using var client = CreateClient(
            [Provider(stalling, "stalling"), Provider(missing, "missing")],
            recoveryBudget: TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    ["s1", "s2"], depth: 2, fallbackConcurrency: 2, null, cancellation.Token)
                .WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task BackupGateContentionCannotProduceMissingVerdicts()
    {
        // Every lane misses on the primary and descends into the BackupOnly
        // provider, which never answers. Whether a lane wins a recovery slot
        // (and times out) or is turned away at the gate, the outcome must be
        // unverifiable — never a confirmed-missing article.
        var primary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var backup = new VerdictStatClient((_, _) => StatAnswer.Stall);
        using var client = CreateClient([
            Provider(primary, "primary"),
            Provider(backup, "backup", ProviderType.BackupOnly),
        ]);
        var segments = Enumerable.Range(0, 8).Select(i => $"s{i}").ToArray();

        await Assert.ThrowsAsync<UsenetArticleUnverifiableException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    segments, depth: 1, fallbackConcurrency: 8, null, CancellationToken.None)
                .WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task ZeroCoverageQualificationChecksColdBackupBeforeBulkRecovery()
    {
        // Two responsive zero-coverage primaries trigger the bounded BackupOnly
        // rescue sample. Once that sample finds coverage, only the unsampled
        // articles continue into the normal bulk path.
        var firstPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var secondPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var backup = new VerdictStatClient((_, _) => StatAnswer.Found);
        using var client = CreateClient([
            Provider(firstPrimary, "first-primary", maxConnections: 8),
            Provider(secondPrimary, "second-primary", maxConnections: 8),
            Provider(backup, "backup", ProviderType.BackupOnly, maxConnections: 4),
        ]);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();

        await client.CheckAllSegmentsPipelinedAsync(
                segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Equal(2, backup.Calls);
        Assert.Equal([32, segments.Length - 32], backup.BatchSizes);
    }

    [Fact]
    public async Task ColdBackupRecoveryRetriesZeroResponseStallOnFreshSocket()
    {
        // Every primary answers "missing", then the cold BackupOnly sample
        // socket returns no STAT response. The pool rotates that socket and the
        // bounded sample retry finds coverage before bulk health proceeds.
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        var firstPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var secondPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var backup = new VerdictStatClient(
            (_, call) => call == 1 ? StatAnswer.TransportStall : StatAnswer.Found);
        var backupProvider = Provider(
            backup, "backup", ProviderType.BackupOnly, maxConnections: 1);
        using var scope = tracker.BeginScope(queueId);
        using var client = CreateClient([
                Provider(firstPrimary, "first-primary", maxConnections: 8),
                Provider(secondPrimary, "second-primary", maxConnections: 8),
                backupProvider,
            ],
            usageTracker: tracker);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();

        await client.CheckAllSegmentsPipelinedAsync(
                segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Equal(3, backup.Calls);
        var snapshot = Assert.IsType<HealthCheckUsageSnapshot>(
            tracker.SnapshotHealthCheck(queueId));
        var backupStats = Assert.Single(
            snapshot.Providers, provider => provider.ProviderId == "backup");
        Assert.Equal(32, backupStats.ProbeFound);
        Assert.Equal(1, backupStats.Batches);
        Assert.Equal(0, backupStats.Failures);
        Assert.Equal(segments.Length - 32, backupStats.Found);
        Assert.Null(tracker.SnapshotRecoveryNotice(queueId));
    }

    [Fact]
    public async Task TinyBackupRecoveryUsesSequentialStatAfterTwoSilentSockets()
    {
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        var firstPrimary = new VerdictStatClient(PrimaryAnswer);
        var secondPrimary = new VerdictStatClient(PrimaryAnswer);
        var backup = new VerdictStatClient(
            (_, _) => StatAnswer.TransportStall,
            sequentialScript: (_, _) => StatAnswer.Found);
        using var scope = tracker.BeginScope(queueId);
        using var client = CreateClient([
                Provider(firstPrimary, "first-primary", maxConnections: 8),
                Provider(secondPrimary, "second-primary", maxConnections: 8),
                Provider(backup, "backup", ProviderType.BackupOnly, maxConnections: 1),
            ],
            usageTracker: tracker);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();
        segments[1] = "recovery-1";
        segments[2] = "recovery-2";

        await client.CheckAllSegmentsPipelinedAsync(
                segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Equal(2, backup.Calls);
        Assert.Equal(2, backup.SequentialCalls);
        Assert.Equal([2, 2], backup.BatchSizes);
        var snapshot = Assert.IsType<HealthCheckUsageSnapshot>(
            tracker.SnapshotHealthCheck(queueId));
        var backupStats = Assert.Single(
            snapshot.Providers, provider => provider.ProviderId == "backup");
        Assert.Equal(3, backupStats.Batches);
        Assert.Equal(2, backupStats.Failures);
        Assert.Equal(2, backupStats.Found);
        Assert.Null(tracker.SnapshotRecoveryNotice(queueId));

        static StatAnswer PrimaryAnswer(string segmentId, int _) =>
            segmentId.StartsWith("recovery-", StringComparison.Ordinal)
                ? StatAnswer.Missing
                : StatAnswer.Found;
    }

    [Fact]
    public async Task TinyBackupSequentialRecoveryFailureIsAttemptedOnlyOnce()
    {
        var firstPrimary = new VerdictStatClient(PrimaryAnswer);
        var secondPrimary = new VerdictStatClient(PrimaryAnswer);
        var backup = new VerdictStatClient(
            (_, _) => StatAnswer.TransportStall,
            sequentialScript: (_, _) => StatAnswer.Error);
        using var client = CreateClient([
            Provider(firstPrimary, "first-primary", maxConnections: 8),
            Provider(secondPrimary, "second-primary", maxConnections: 8),
            Provider(backup, "backup", ProviderType.BackupOnly, maxConnections: 1),
        ]);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();
        segments[1] = "recovery-1";
        segments[2] = "recovery-2";

        var exception = await Assert.ThrowsAsync<UsenetArticleUnverifiableException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
                .WaitAsync(TestTimeout));

        Assert.Equal(2, backup.Calls);
        Assert.Equal(1, backup.SequentialCalls);
        Assert.Contains("backup", exception.UnavailableProviders);

        static StatAnswer PrimaryAnswer(string segmentId, int _) =>
            segmentId.StartsWith("recovery-", StringComparison.Ordinal)
                ? StatAnswer.Missing
                : StatAnswer.Found;
    }

    [Fact]
    public async Task ZeroCoverageFastFailStopsAfterBackupFreshSocketRetry()
    {
        var firstPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var secondPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var backup = new VerdictStatClient((_, _) => StatAnswer.TransportStall);
        using var client = CreateClient([
            Provider(firstPrimary, "first-primary", maxConnections: 8),
            Provider(secondPrimary, "second-primary", maxConnections: 8),
            Provider(backup, "backup", ProviderType.BackupOnly, maxConnections: 1),
        ]);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
                .WaitAsync(TestTimeout));

        Assert.Equal(2, backup.Calls);
        Assert.Equal(0, backup.SequentialCalls);
    }

    [Fact]
    public async Task BackupSampleAndBulkFindsAppearInProviderStatistics()
    {
        // A warm BackupOnly socket stalls during the zero-coverage rescue
        // sample, then its fresh-socket retry succeeds. Probe and bulk findings
        // remain distinct in the provider statistics and cover the whole job.
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        var firstPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var secondPrimary = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var backup = new VerdictStatClient(
            (_, call) => call == 1 ? StatAnswer.Stall : StatAnswer.Found);
        var backupProvider = Provider(
            backup, "backup", ProviderType.BackupOnly, maxConnections: 4);
        await backupProvider.PrewarmAsync(1, CancellationToken.None);
        using var scope = tracker.BeginScope(queueId);
        using var client = CreateClient([
                Provider(firstPrimary, "first-primary", maxConnections: 8),
                Provider(secondPrimary, "second-primary", maxConnections: 8),
                backupProvider,
            ],
            usageTracker: tracker);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();

        await client.CheckAllSegmentsPipelinedAsync(
                segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
            .WaitAsync(TestTimeout);

        var snapshot = Assert.IsType<HealthCheckUsageSnapshot>(
            tracker.SnapshotHealthCheck(queueId));
        var backupStats = Assert.Single(snapshot.Providers, provider => provider.ProviderId == "backup");
        Assert.Equal(32, backupStats.ProbeFound);
        Assert.Equal(segments.Length - 32, backupStats.Found);
        Assert.Equal(0, backupStats.Missing);
        Assert.Equal(0, backupStats.Failures);
        Assert.Equal(1, backupStats.Batches);
        Assert.Equal(3, backup.Calls);
        Assert.Null(tracker.SnapshotRecoveryNotice(queueId));
    }

    [Fact]
    public async Task PartialPipelineResponsesRemainResolved()
    {
        // The first provider answers one segment and then dies mid-batch. Its
        // received answer must survive; only the unanswered tail is retried on
        // the second provider.
        var partial = new VerdictStatClient(
            (segmentId, _) => segmentId == "s1" ? StatAnswer.Found : StatAnswer.Error);
        var complete = new VerdictStatClient((_, _) => StatAnswer.Found);
        using var client = CreateClient(
            [Provider(partial, "partial"), Provider(complete, "complete")]);

        var results = new List<PipelinedStatResult>();
        await foreach (var result in client.StatsPipelinedAsync(
                           ["s1", "s2"], 2, CancellationToken.None))
            results.Add(result);

        Assert.All(results, result => Assert.True(result.Exists));
        Assert.Equal(1, partial.Calls);
        Assert.Equal(1, complete.Calls);
    }

    [Fact]
    public async Task TwoZeroCoverageProbesFastFailDespiteAnotherProbeTimingOut()
    {
        // The captured-run pathology: independent responsive providers probe
        // 0/32 while another provider's probe times out. This is enough evidence
        // to fail before the full-release sweep and recovery pass.
        var firstResponsive = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var secondResponsive = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var unresponsive = new VerdictStatClient((_, _) => StatAnswer.Stall);
        using var client = CreateClient([
            Provider(firstResponsive, "first-responsive", maxConnections: 8),
            Provider(secondResponsive, "second-responsive", maxConnections: 8),
            Provider(unresponsive, "unresponsive", maxConnections: 8),
        ]);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
            client.CheckAllSegmentsPipelinedAsync(
                    segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
                .WaitAsync(TestTimeout));

        Assert.Equal(1, unresponsive.Calls);
    }

    [Fact]
    public async Task ZeroResponseProbeRetriesFreshSocketBeforeQuarantiningProvider()
    {
        // One stale socket must not quarantine an otherwise fast, full-coverage
        // provider. One of the first two 16-command qualification lanes stalls;
        // after that socket is evicted, the fresh single-lane retry succeeds and
        // the provider carries primary lanes.
        var flaky = new VerdictStatClient(
            (_, call) => call == 1 ? StatAnswer.Stall : StatAnswer.Found);
        var missing = new VerdictStatClient((_, _) => StatAnswer.Missing);
        var flakyProvider = Provider(flaky, "flaky", maxConnections: 8);
        await flakyProvider.PrewarmAsync(2, CancellationToken.None);
        using var client = CreateClient([
            flakyProvider,
            Provider(missing, "missing", maxConnections: 8),
        ]);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();

        await client.CheckAllSegmentsPipelinedAsync(
                segments, depth: 32, fallbackConcurrency: 8, null, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(flaky.Calls > 3);
        Assert.Equal([16, 16, 32], flaky.BatchSizes.Take(3));
    }

    [Fact]
    public async Task LargeIndeterminateRecoveryUsesSeveralEstablishedConnections()
    {
        // At two milliseconds per answer, one socket needs more than four seconds
        // for this recovery set. The four-second operation budget is deliberately
        // impossible serially but comfortable when the established idle pool is
        // partitioned across bounded recovery lanes.
        var recovering = new VerdictStatClient(
            (_, call) => call <= 4 ? StatAnswer.Stall : StatAnswer.Found,
            TimeSpan.FromMilliseconds(2));
        var recoveringProvider = Provider(recovering, "recovering", maxConnections: 8);
        await recoveringProvider.PrewarmAsync(8, CancellationToken.None);

        var missing = new VerdictStatClient((_, _) => StatAnswer.Missing);
        using var client = CreateClient([
                recoveringProvider,
                Provider(missing, "missing", maxConnections: 8),
            ],
            recoveryBudget: TimeSpan.FromSeconds(4));
        var segments = Enumerable.Range(0, 2048).Select(i => $"s{i}").ToArray();

        await client.CheckAllSegmentsPipelinedAsync(
                segments, depth: 32, fallbackConcurrency: 8, null, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.True(recovering.MaxConcurrentCalls >= 4);
        Assert.Equal(segments.Length, recovering.BatchSizes.Skip(4).Sum());
    }

    [Fact]
    public async Task ProviderDyingDuringBulkWorkIsQuarantinedAfterBoundedFailures()
    {
        // Passes qualification, then stops answering. After the quarantine
        // threshold it must leave the lane rotation instead of burning an
        // attempt timeout in every remaining batch.
        var dying = new VerdictStatClient(
            (_, call) => call <= 1 ? StatAnswer.Found : StatAnswer.Stall);
        var healthy = new VerdictStatClient((_, _) => StatAnswer.Found);
        using var client = CreateClient([
            Provider(dying, "dying", maxConnections: 8),
            Provider(healthy, "healthy", maxConnections: 8),
        ]);
        var segments = Enumerable.Range(0, 300).Select(i => $"s{i}").ToArray();

        await client.CheckAllSegmentsPipelinedAsync(
                segments, depth: 16, fallbackConcurrency: 8, null, CancellationToken.None)
            .WaitAsync(TestTimeout);

        // Probe + at most a burst of concurrent lane picks before quarantine;
        // far fewer than the ~19 batches the workload contains.
        Assert.InRange(dying.Calls, 1, 16);
    }

    private static MultiProviderNntpClient CreateClient(
        List<MultiConnectionNntpClient> providers,
        TimeSpan? recoveryBudget = null,
        ProviderUsageTracker? usageTracker = null)
        => new(
            providers,
            usageTracker ?? new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(100),
            providerOperationTimeout: TimeSpan.FromMilliseconds(400),
            indeterminateRecoveryBudget: recoveryBudget ?? TimeSpan.FromMilliseconds(400),
            bulkStatProbeTimeout: TimeSpan.FromMilliseconds(75));

    private static MultiConnectionNntpClient Provider(
        INntpClient transport,
        string host,
        ProviderType type = ProviderType.Pooled,
        int maxConnections = 4)
        => new(
            new ConnectionPool<INntpClient>(
                maxConnections, _ => ValueTask.FromResult(transport)),
            type,
            new ProviderCircuitBreaker(host),
            host,
            byteLimit: null,
            bytesUsedOffset: 0,
            priority: 0,
            prepOnly: false,
            prepSpreadEnabled: true);

    private enum StatAnswer
    {
        Found,
        Missing,
        Stall,
        TransportStall,
        Error,
    }

    /// <summary>
    /// Pipelined STAT transport scripted per (segment id, per-provider call
    /// number). Stall waits on the token; Error throws mid-stream after any
    /// previously yielded results.
    /// </summary>
    private sealed class VerdictStatClient(
        Func<string, int, StatAnswer> script,
        TimeSpan? resultDelay = null,
        Func<string, int, StatAnswer>? sequentialScript = null) : NntpClient
    {
        private int _calls;
        private int _sequentialCalls;
        private int _activeCalls;
        private int _maxConcurrentCalls;
        private readonly System.Collections.Concurrent.ConcurrentQueue<int> _batchSizes = new();
        public int Calls => Volatile.Read(ref _calls);
        public int SequentialCalls => Volatile.Read(ref _sequentialCalls);
        public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);
        public int[] BatchSizes => _batchSizes.ToArray();

        public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
            IReadOnlyList<string> segmentIds,
            int depth,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            _batchSizes.Enqueue(segmentIds.Count);
            var active = Interlocked.Increment(ref _activeCalls);
            int observed;
            while (active > (observed = Volatile.Read(ref _maxConcurrentCalls)))
                Interlocked.CompareExchange(ref _maxConcurrentCalls, active, observed);
            try
            {
                foreach (var segmentId in segmentIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (script(segmentId, call))
                    {
                        case StatAnswer.Stall:
                            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                            break;
                        case StatAnswer.TransportStall:
                            throw new UsenetPipelinedStatStalledException(
                                "Scripted zero-response transport stall.", 0);
                        case StatAnswer.Error:
                            throw new IOException("Scripted mid-batch failure.");
                        case StatAnswer.Found:
                            if (resultDelay is not null)
                                await Task.Delay(resultDelay.Value, cancellationToken);
                            yield return new PipelinedStatResult { SegmentId = segmentId, Exists = true };
                            break;
                        case StatAnswer.Missing:
                            if (resultDelay is not null)
                                await Task.Delay(resultDelay.Value, cancellationToken);
                            yield return new PipelinedStatResult { SegmentId = segmentId, Exists = false };
                            break;
                    }

                    await Task.Yield();
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeCalls);
            }
        }

        public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            var answer = sequentialScript?.Invoke(
                segmentId, Interlocked.Increment(ref _sequentialCalls))
                ?? throw new NotSupportedException();
            switch (answer)
            {
                case StatAnswer.Stall:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Unreachable.");
                case StatAnswer.TransportStall:
                case StatAnswer.Error:
                    throw new IOException("Scripted sequential STAT failure.");
                case StatAnswer.Found:
                    return new UsenetStatResponse
                    {
                        ResponseCode = (int)UsenetResponseType.ArticleExists,
                        ResponseMessage = "223 article exists",
                        ArticleExists = true,
                    };
                case StatAnswer.Missing:
                    return new UsenetStatResponse
                    {
                        ResponseCode = (int)UsenetResponseType.NoArticleWithThatMessageId,
                        ResponseMessage = "430 no such article",
                        ArticleExists = false,
                    };
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
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
