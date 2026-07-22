using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
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
    public async Task PrepFallbackBurstRemainsHardCappedWhenMoreIdleSocketsAppear()
    {
        const int totalRequests = 20;
        var primaryTransport = new BodyClient(articleFound: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            totalRequests, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(totalRequests);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var backupTransport = new BlockingBodyClient();
        await using var backupPool = new ConnectionPool<INntpClient>(
            totalRequests, _ => ValueTask.FromResult<INntpClient>(backupTransport));
        using var backup = CreateProvider(
            backupPool, "backup", 1, type: ProviderType.BackupOnly);
        using var client = new MultiProviderNntpClient(
            [primary, backup],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromSeconds(2),
            providerOperationTimeout: TimeSpan.FromSeconds(4));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        using var fallbackContext = new PrepFallbackContext();
        fallbackContext.MarkResponsive(backup);
        prepCts.SetContext(fallbackContext);

        var requests = Enumerable.Range(0, totalRequests)
            .Select(async index =>
            {
                var response = await client.DecodedBodyAsync(
                    $"segment-{index}", prepCts.Token);
                await response.Stream.DisposeAsync();
            })
            .ToArray();

        try
        {
            await WaitUntilAsync(
                () => backupTransport.CurrentCommands ==
                      PrepFallbackContext.MaxConcurrentRequestsPerHost,
                TimeSpan.FromSeconds(2));
            await Task.Delay(75);

            Assert.Equal(
                PrepFallbackContext.MaxConcurrentRequestsPerHost,
                backupTransport.MaximumConcurrentCommands);

            // Eight more authenticated sockets appear while twelve fallback
            // commands are in flight. Warm capacity must not defeat the
            // fallback-specific concurrency ceiling.
            await backupPool.PrewarmAsync(totalRequests);
            await Task.Delay(75);
            Assert.Equal(
                PrepFallbackContext.MaxConcurrentRequestsPerHost,
                backupTransport.CurrentCommands);
            Assert.Equal(
                PrepFallbackContext.MaxConcurrentRequestsPerHost,
                backupTransport.MaximumConcurrentCommands);
        }
        finally
        {
            backupTransport.Release();
        }

        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PrepFallbackStartsWithOneProbationLaneThenExpandsAfterResponse()
    {
        await using var pool = new ConnectionPool<INntpClient>(
            20, _ => throw new InvalidOperationException("No connection should be created."));
        using var provider = CreateProvider(
            pool, "fallback", 0, type: ProviderType.BackupOnly);
        using var context = new PrepFallbackContext();

        var first = await context.EnterAsync(provider, CancellationToken.None);
        var secondTask = context.EnterAsync(provider, CancellationToken.None).AsTask();
        await Task.Delay(75);
        Assert.False(secondTask.IsCompleted);

        context.MarkResponsive(provider);
        var admitted = new List<IDisposable?> { first, await secondTask };
        admitted.AddRange(await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => context.EnterAsync(provider, CancellationToken.None).AsTask())));

        var thirteenthTask = context.EnterAsync(provider, CancellationToken.None).AsTask();
        await Task.Delay(75);
        Assert.False(thirteenthTask.IsCompleted);

        admitted[0]!.Dispose();
        admitted.Add(await thirteenthTask.WaitAsync(TimeSpan.FromSeconds(1)));
        foreach (var lease in admitted.Skip(1)) lease!.Dispose();
    }

    [Fact]
    public async Task PrepFallbackAdmissionWaitDoesNotConsumeProviderAttemptWindow()
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

        var finalTransport = new BodyClient(articleFound: false);
        await using var finalPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(finalTransport));
        using var final = CreateProvider(
            finalPool, "final", 2, type: ProviderType.BackupOnly);

        using var client = new MultiProviderNntpClient(
            [primary, backup, final],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromMilliseconds(500));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        prepCts.SetContext(new PrepFallbackContext());

        var first = await client.DecodedBodyAsync("first", prepCts.Token);
        var secondTask = client.DecodedBodyAsync("second", prepCts.Token);

        try
        {
            await Task.Delay(125);
            Assert.False(secondTask.IsCompleted);
        }
        finally
        {
            await first.Stream.DisposeAsync();
        }

        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
        await second.Stream.DisposeAsync();

        Assert.Equal(2, backupTransport.BodyCalls);
        Assert.Equal(0, finalTransport.BodyCalls);
    }

    [Fact]
    public async Task PrepFallbackGetsFreshCommandWindowAfterConnectionAcquisition()
    {
        var primaryTransport = new BodyClient(articleFound: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var connectionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowConnection = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var backupTransport = new PhaseControlledBodyClient();
        await using var backupPool = new ConnectionPool<INntpClient>(
            1,
            async cancellationToken =>
            {
                connectionStarted.TrySetResult();
                await allowConnection.Task.WaitAsync(cancellationToken);
                return backupTransport;
            });
        using var backup = CreateProvider(
            backupPool, "backup", 1, type: ProviderType.BackupOnly);

        var finalTransport = new BodyClient(articleFound: false);
        await using var finalPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(finalTransport));
        using var final = CreateProvider(
            finalPool, "final", 2, type: ProviderType.BackupOnly);

        using var client = new MultiProviderNntpClient(
            [primary, backup, final],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(400),
            providerOperationTimeout: TimeSpan.FromSeconds(2));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        prepCts.SetContext(new PrepFallbackContext());

        var request = client.DecodedBodyAsync("segment", prepCts.Token);
        await connectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(250);
        allowConnection.TrySetResult();
        await backupTransport.CommandStarted.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(250);
        backupTransport.Release();

        var response = await request.WaitAsync(TimeSpan.FromSeconds(1));
        await response.Stream.DisposeAsync();

        Assert.Equal(1, backupTransport.BodyCalls);
        Assert.Equal(0, finalTransport.BodyCalls);
    }

    [Fact]
    public async Task CallerCancellationInterruptsPrepFallbackAdmissionWait()
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
            [primary, backup],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(100),
            providerOperationTimeout: TimeSpan.FromSeconds(1));

        var fallbackContext = new PrepFallbackContext();
        using var firstCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        firstCts.SetContext(fallbackContext);
        var first = await client.DecodedBodyAsync("first", firstCts.Token);

        using var callerCancellation = new CancellationTokenSource();
        using var secondCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation.Token);
        secondCts.SetContext(fallbackContext);
        var secondTask = client.DecodedBodyAsync("second", secondCts.Token);

        try
        {
            callerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                secondTask.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await first.Stream.DisposeAsync();
        }
    }

    [Fact]
    public async Task OneStalledConnectionDoesNotPoisonAnotherAccountOnSameHost()
    {
        var primaryTransport = new BodyClient(articleFound: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        const string stalledHost = "shared-stalled-host";
        var stalledTransport = new BodyClient(stall: true);
        await using var stalledPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(stalledTransport));
        await stalledPool.PrewarmAsync(1);
        using var stalled = CreateProvider(
            stalledPool, stalledHost, 1, type: ProviderType.BackupOnly);

        var siblingTransport = new BodyClient();
        await using var siblingPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(siblingTransport));
        await siblingPool.PrewarmAsync(1);
        using var sibling = CreateProvider(
            siblingPool, stalledHost, 2, type: ProviderType.BackupOnly);

        var finalTransport = new BodyClient();
        await using var finalPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(finalTransport));
        await finalPool.PrewarmAsync(1);
        using var final = CreateProvider(
            finalPool, "final", 3, type: ProviderType.BackupOnly);

        using var client = new MultiProviderNntpClient(
            [primary, stalled, sibling, final],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(75),
            providerOperationTimeout: TimeSpan.FromSeconds(1));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        prepCts.SetContext(new PrepFallbackContext());

        var response = await client.DecodedBodyAsync("segment", prepCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await response.Stream.DisposeAsync();

        Assert.Equal(1, stalledTransport.BodyCalls);
        Assert.Equal(1, siblingTransport.BodyCalls);
        Assert.Equal(0, finalTransport.BodyCalls);
    }

    [Fact]
    public async Task ConcurrentStalledFallbackCommandsEachContinueToNextProvider()
    {
        const int requestCount = PrepFallbackContext.MaxConcurrentRequestsPerHost;
        var primaryTransport = new BodyClient(articleFound: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            requestCount, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(requestCount);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var stalledTransport = new BodyClient(stall: true);
        await using var stalledPool = new ConnectionPool<INntpClient>(
            requestCount, _ => ValueTask.FromResult<INntpClient>(stalledTransport));
        await stalledPool.PrewarmAsync(requestCount);
        using var stalled = CreateProvider(
            stalledPool, "stalled", 1, type: ProviderType.BackupOnly);

        var finalTransport = new BodyClient();
        await using var finalPool = new ConnectionPool<INntpClient>(
            requestCount, _ => ValueTask.FromResult<INntpClient>(finalTransport));
        await finalPool.PrewarmAsync(requestCount);
        using var final = CreateProvider(
            finalPool, "final", 2, type: ProviderType.BackupOnly);

        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using var client = new MultiProviderNntpClient(
            [primary, stalled, final],
            tracker,
            providerAttemptTimeout: TimeSpan.FromMilliseconds(150),
            providerOperationTimeout: TimeSpan.FromSeconds(2));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        using var fallbackContext = new PrepFallbackContext();
        fallbackContext.MarkResponsive(stalled);
        prepCts.SetContext(fallbackContext);

        using (tracker.BeginScope(queueId))
        using (tracker.BeginPrepAttemptCapture())
        {
            var requests = Enumerable.Range(0, requestCount).Select(async index =>
            {
                var response = await client.DecodedBodyAsync(
                    $"segment-{index}", prepCts.Token);
                await response.Stream.DisposeAsync();
            });
            await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(requestCount, stalledTransport.BodyCalls);
        Assert.Equal(requestCount, finalTransport.BodyCalls);
        var attempts = tracker.SnapshotPrepAttempts(queueId);
        Assert.Equal(requestCount, attempts["stalled"].Attempts);
        Assert.Equal(requestCount, attempts["stalled"].Timeouts);
    }

    [Fact]
    public async Task PrepFallbackMovesPastOneStalledSocketWithoutRetryingTheHost()
    {
        var primaryTransport = new BodyClient(articleFound: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var flakyTransport = new FirstCallStallsBodyClient();
        await using var flakyPool = new ConnectionPool<INntpClient>(
            2, _ => ValueTask.FromResult<INntpClient>(flakyTransport));
        await flakyPool.PrewarmAsync(2);
        using var flaky = CreateProvider(
            flakyPool, "flaky", 1, type: ProviderType.BackupOnly);

        var finalTransport = new BodyClient();
        await using var finalPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(finalTransport));
        await finalPool.PrewarmAsync(1);
        using var final = CreateProvider(
            finalPool, "final", 2, type: ProviderType.BackupOnly);

        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using var client = new MultiProviderNntpClient(
            [primary, flaky, final],
            tracker,
            providerAttemptTimeout: TimeSpan.FromMilliseconds(100),
            providerOperationTimeout: TimeSpan.FromSeconds(1));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        using var fallbackContext = new PrepFallbackContext();
        prepCts.SetContext(fallbackContext);

        using (tracker.BeginScope(queueId))
        using (tracker.BeginPrepAttemptCapture())
        {
            var response = await client.DecodedBodyAsync("segment", prepCts.Token)
                .WaitAsync(TimeSpan.FromSeconds(1));
            await response.Stream.DisposeAsync();
        }

        Assert.Equal(1, flakyTransport.BodyCalls);
        Assert.Equal(1, finalTransport.BodyCalls);
        var attempts = tracker.SnapshotPrepAttempts(queueId);
        Assert.Equal(1, attempts["flaky"].Attempts);
        Assert.Equal(1, attempts["flaky"].Timeouts);
    }

    [Fact]
    public async Task LaterPrepProviderGetsItsOwnAttemptWindow()
    {
        var primaryTransport = new BodyClient(articleFound: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var stalledTransport = new BodyClient(stall: true);
        await using var stalledPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(stalledTransport));
        await stalledPool.PrewarmAsync(1);
        using var stalled = CreateProvider(
            stalledPool, "stalled", 1, type: ProviderType.BackupOnly);

        var lateTransport = new BodyClient();
        await using var latePool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(lateTransport));
        await latePool.PrewarmAsync(1);
        using var late = CreateProvider(
            latePool, "late", 2, type: ProviderType.BackupOnly);

        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using var client = new MultiProviderNntpClient(
            [primary, stalled, late],
            tracker,
            providerAttemptTimeout: TimeSpan.FromMilliseconds(200),
            providerOperationTimeout: TimeSpan.FromMilliseconds(250));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        using var fallbackContext = new PrepFallbackContext();
        prepCts.SetContext(fallbackContext);

        using (tracker.BeginScope(queueId))
        using (tracker.BeginPrepAttemptCapture())
        {
            var response = await client.DecodedBodyAsync("segment", prepCts.Token)
                .WaitAsync(TimeSpan.FromSeconds(1));
            await response.Stream.DisposeAsync();
        }

        Assert.Equal(1, stalledTransport.BodyCalls);
        Assert.Equal(1, lateTransport.BodyCalls);
        var attempts = tracker.SnapshotPrepAttempts(queueId);
        Assert.Equal(1, attempts["stalled"].Timeouts);
        Assert.Equal(1, attempts["late"].Attempts);
    }

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
        var tracker = new ProviderUsageTracker();
        var queueId = Guid.NewGuid();
        using var client = new MultiProviderNntpClient([primary, backup], tracker);

        using (tracker.BeginScope(queueId))
        using (tracker.BeginPrepAttemptCapture())
        {
            var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
            await response.Stream.DisposeAsync();
        }

        Assert.Equal(1, primaryTransport.BodyCalls);
        Assert.Equal(1, backupTransport.BodyCalls);
        var attempts = tracker.SnapshotPrepAttempts(queueId);
        Assert.Equal(1, attempts["primary"].Attempts);
        Assert.Equal(1, attempts["primary"].Missing);
        Assert.Equal(1, attempts["backup"].Attempts);
        Assert.Equal(0, attempts["backup"].Missing);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepMissingVerdictStaysRetryableWhenAnyProviderIsUnavailable(
        bool stalledProviderFirst)
    {
        var stalledTransport = new BodyClient(stall: true);
        await using var stalledPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(stalledTransport));
        await stalledPool.PrewarmAsync(1);
        using var stalled = CreateProvider(stalledPool, "stalled", 0);

        var missingTransport = new BodyClient(articleFound: false);
        await using var missingPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(missingTransport));
        await missingPool.PrewarmAsync(1);
        using var missing = CreateProvider(missingPool, "missing", 1);
        var providers = stalledProviderFirst
            ? new List<MultiConnectionNntpClient> { stalled, missing }
            : [missing, stalled];
        using var client = new MultiProviderNntpClient(
            providers,
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<CouldNotConnectToUsenetException>(() =>
            client.DecodedBodyAsync("segment", CancellationToken.None));
    }

    [Fact]
    public async Task PrepConfirmsMissingOnlyWhenEveryProviderAnswersMissing()
    {
        var firstTransport = new BodyClient(articleFound: false);
        await using var firstPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(firstTransport));
        await firstPool.PrewarmAsync(1);
        using var first = CreateProvider(firstPool, "first", 0);

        var secondTransport = new BodyClient(articleFound: false);
        await using var secondPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(secondTransport));
        await secondPool.PrewarmAsync(1);
        using var second = CreateProvider(secondPool, "second", 1);
        using var client = new MultiProviderNntpClient(
            [first, second],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAsync<UsenetArticleNotFoundException>(() =>
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

    [Fact]
    public async Task PrepFallbackUsesStatBeforeDownloadingArticle()
    {
        var primaryTransport = new PrepArticleClient(
            statExists: true, articleExists: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var missingTransport = new PrepArticleClient(
            statExists: false, articleExists: true);
        await using var missingPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(missingTransport));
        await missingPool.PrewarmAsync(1);
        using var missing = CreateProvider(
            missingPool, "missing", 1, type: ProviderType.BackupOnly);

        var rescueTransport = new PrepArticleClient(
            statExists: true, articleExists: true);
        await using var rescuePool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(rescueTransport));
        await rescuePool.PrewarmAsync(1);
        using var rescue = CreateProvider(
            rescuePool, "rescue", 2, type: ProviderType.BackupOnly);

        using var client = new MultiProviderNntpClient(
            [primary, missing, rescue],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(250),
            providerOperationTimeout: TimeSpan.FromSeconds(1));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        prepCts.SetContext(new PrepFallbackContext());

        var response = await client.DecodedArticleAsync("segment", prepCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await response.Stream.DisposeAsync();

        Assert.Equal(0, primaryTransport.StatCalls);
        Assert.Equal(1, primaryTransport.ArticleCalls);
        Assert.Equal(1, missingTransport.StatCalls);
        Assert.Equal(0, missingTransport.ArticleCalls);
        Assert.Equal(1, rescueTransport.StatCalls);
        Assert.Equal(1, rescueTransport.ArticleCalls);
    }

    [Fact]
    public async Task PrepFallbackStatTimeoutStillMovesToLaterProvider()
    {
        var primaryTransport = new PrepArticleClient(
            statExists: true, articleExists: false);
        await using var primaryPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(primaryTransport));
        await primaryPool.PrewarmAsync(1);
        using var primary = CreateProvider(primaryPool, "primary", 0);

        var stalledTransport = new PrepArticleClient(
            statExists: true, articleExists: true, stallStat: true);
        await using var stalledPool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(stalledTransport));
        await stalledPool.PrewarmAsync(1);
        using var stalled = CreateProvider(
            stalledPool, "stalled", 1, type: ProviderType.BackupOnly);

        var rescueTransport = new PrepArticleClient(
            statExists: true, articleExists: true);
        await using var rescuePool = new ConnectionPool<INntpClient>(
            1, _ => ValueTask.FromResult<INntpClient>(rescueTransport));
        await rescuePool.PrewarmAsync(1);
        using var rescue = CreateProvider(
            rescuePool, "rescue", 2, type: ProviderType.BackupOnly);

        using var client = new MultiProviderNntpClient(
            [primary, stalled, rescue],
            new ProviderUsageTracker(),
            providerAttemptTimeout: TimeSpan.FromMilliseconds(50),
            providerOperationTimeout: TimeSpan.FromSeconds(1));
        using var prepCts =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        prepCts.SetContext(new PrepFallbackContext());

        var response = await client.DecodedArticleAsync("segment", prepCts.Token)
            .WaitAsync(TimeSpan.FromSeconds(1));
        await response.Stream.DisposeAsync();

        Assert.Equal(1, stalledTransport.StatCalls);
        Assert.Equal(0, stalledTransport.ArticleCalls);
        Assert.Equal(1, rescueTransport.StatCalls);
        Assert.Equal(1, rescueTransport.ArticleCalls);
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

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The expected test condition was not reached in time.");
            await Task.Delay(10);
        }
    }

    private sealed class BlockingBodyClient : NntpClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bodyCalls;
        private int _currentCommands;
        private int _maximumConcurrentCommands;

        public int CurrentCommands => Volatile.Read(ref _currentCommands);
        public int MaximumConcurrentCommands => Volatile.Read(ref _maximumConcurrentCommands);

        public void Release() => _release.TrySetResult();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _bodyCalls);
            var current = Interlocked.Increment(ref _currentCommands);
            while (true)
            {
                var maximum = Volatile.Read(ref _maximumConcurrentCommands);
                if (current <= maximum ||
                    Interlocked.CompareExchange(
                        ref _maximumConcurrentCommands, current, maximum) == maximum)
                    break;
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                callback?.Invoke(ArticleBodyResult.Retrieved);
                return new UsenetDecodedBodyResponse
                {
                    SegmentId = segmentId,
                    ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                    ResponseMessage = "222 body follows",
                    Stream = new YencStream(new MemoryStream()),
                };
            }
            finally
            {
                Interlocked.Decrement(ref _currentCommands);
            }
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }

    private sealed class PhaseControlledBodyClient : NntpClient
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _bodyCalls;

        public int BodyCalls => Volatile.Read(ref _bodyCalls);
        public Task CommandStarted => _commandStarted.Task;
        private readonly TaskCompletionSource _commandStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _bodyCalls);
            _commandStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            callback?.Invoke(ArticleBodyResult.Retrieved);
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new YencStream(new MemoryStream()),
            };
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }

    private sealed class FirstCallStallsBodyClient : NntpClient
    {
        private int _bodyCalls;
        public int BodyCalls => Volatile.Read(ref _bodyCalls);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedBodyAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _bodyCalls) == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            callback?.Invoke(ArticleBodyResult.Retrieved);
            return new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new YencStream(new MemoryStream()),
            };
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }

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

    private sealed class PrepArticleClient(
        bool statExists,
        bool articleExists,
        bool stallStat = false) : NntpClient
    {
        private int _statCalls;
        private int _articleCalls;

        public int StatCalls => Volatile.Read(ref _statCalls);
        public int ArticleCalls => Volatile.Read(ref _articleCalls);

        public override async Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _statCalls);
            if (stallStat)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new UsenetStatResponse
            {
                ResponseCode = statExists
                    ? (int)UsenetResponseType.ArticleExists
                    : (int)UsenetResponseType.NoArticleWithThatMessageId,
                ResponseMessage = statExists ? "223 article exists" : "430 no article",
                ArticleExists = statExists,
            };
        }

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _articleCalls);
            if (!articleExists)
                throw new UsenetArticleNotFoundException(segmentId);

            callback?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(new UsenetDecodedArticleResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
                ResponseMessage = "220 article follows",
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(),
                },
                Stream = new YencStream(new MemoryStream()),
            });
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? callback,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override void Dispose()
        {
        }
    }
}
