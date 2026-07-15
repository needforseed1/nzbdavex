using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolTests
{
    [Fact]
    public async Task DisposalLetsBorrowedConnectionFinishBeforeDestroyingIt()
    {
        var disposed = 0;
        var pool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(
                1, () => Interlocked.Increment(ref disposed))));
        var borrowed = await pool.GetConnectionLockAsync(SemaphorePriority.Low);

        await pool.DisposeAsync();
        Assert.Equal(0, disposed);

        borrowed.Dispose();
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task ValidationReplacesOneUnhealthyIdleConnectionWithoutExceedingCapacity()
    {
        var created = 0;
        var disposed = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            2,
            _ => ValueTask.FromResult(new TrackedConnection(
                Interlocked.Increment(ref created), () => Interlocked.Increment(ref disposed))),
            idleTimeout: TimeSpan.FromMinutes(1),
            connectionValidator: (connection, _) => ValueTask.FromResult(connection.Healthy),
            validateAfterIdle: TimeSpan.Zero);

        using (var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        using (var second = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        {
            first.Connection.Healthy = false;
            second.Connection.Healthy = false;
        }

        using var replacement = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        Assert.True(replacement.Connection.Healthy);
        Assert.Equal(3, created);
        Assert.Equal(1, disposed);
        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);
    }

    [Fact]
    public async Task ValidationFailureStartsReplacementBeforeDrainingOtherIdleConnections()
    {
        var created = 0;
        var disposed = 0;
        var replacementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TrackedConnection>(
            2,
            async ct =>
            {
                var id = Interlocked.Increment(ref created);
                if (id > 2)
                {
                    replacementStarted.TrySetResult();
                    await releaseReplacement.Task.WaitAsync(ct);
                }

                return new TrackedConnection(id, () => Interlocked.Increment(ref disposed));
            },
            connectionValidator: (connection, _) => ValueTask.FromResult(connection.Healthy),
            validateAfterIdle: TimeSpan.Zero);

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var second = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        first.Connection.Healthy = false;
        second.Connection.Healthy = false;
        first.Dispose();
        second.Dispose();

        var replacement = pool.GetConnectionLockAsync(SemaphorePriority.Low);
        await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref disposed));
        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);

        releaseReplacement.TrySetResult();
        using var lease = await replacement;
        Assert.Equal(3, lease.Connection.Id);
    }

    [Fact]
    public async Task RefreshWarmConnectionsValidatesAndReplacesTheWarmFloor()
    {
        var created = 0;
        var disposed = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            4,
            _ => ValueTask.FromResult(new TrackedConnection(
                Interlocked.Increment(ref created), () => Interlocked.Increment(ref disposed))),
            connectionValidator: (connection, _) => ValueTask.FromResult(connection.Healthy),
            minimumIdleConnections: 2);

        await pool.PrewarmAsync(2);
        var stale = new List<TrackedConnection>();
        using (var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        using (var second = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        {
            stale.Add(first.Connection);
            stale.Add(second.Connection);
        }
        foreach (var connection in stale) connection.Healthy = false;

        await pool.RefreshWarmConnectionsAsync();

        Assert.Equal(4, created);
        Assert.Equal(2, disposed);
        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(2, pool.IdleConnections);
    }

    [Fact]
    public async Task PrewarmTargetsTotalLiveConnectionsWithoutWaitingForActiveLeases()
    {
        var created = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            4,
            _ => ValueTask.FromResult(new TrackedConnection(
                Interlocked.Increment(ref created), () => { })));

        using var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        using var second = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        await pool.PrewarmAsync(4).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(4, pool.LiveConnections);
        Assert.Equal(2, pool.ActiveConnections);
        Assert.Equal(2, pool.IdleConnections);
    }

    [Fact]
    public async Task PrewarmSuspensionTrimsOnlyIdleConnectionsAndRestoresEligibility()
    {
        await using var pool = new ConnectionPool<TrackedConnection>(
            4,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => { })));

        await pool.PrewarmAsync(4);
        using var active = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        using var suspension = pool.SuspendPrewarming(1, out var closed);

        Assert.Equal(2, closed);
        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(1, pool.ActiveConnections);
        Assert.Equal(1, pool.IdleConnections);

        await pool.PrewarmAsync(4);
        Assert.Equal(2, pool.LiveConnections);

        suspension.Dispose();
        await pool.PrewarmAsync(4);
        Assert.Equal(4, pool.LiveConnections);
        Assert.Equal(1, pool.ActiveConnections);
        Assert.Equal(3, pool.IdleConnections);
    }

    [Fact]
    public async Task PrewarmSuspensionCancelsInFlightSpeculativeConnections()
    {
        var attempt = 0;
        var speculativeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TrackedConnection>(
            4,
            async cancellationToken =>
            {
                var id = Interlocked.Increment(ref attempt);
                if (id == 1) return new TrackedConnection(id, () => { });
                speculativeStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Speculative prewarm should have been cancelled.");
            });

        await pool.PrewarmAsync(1);
        var warming = pool.PrewarmAsync(4);
        await speculativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var suspension = pool.SuspendPrewarming(1, out var closed);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, closed);
        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(1, pool.IdleConnections);
    }

    [Fact]
    public async Task InFlightPrewarmKeepsItsReservedSocketWhenAnotherPoolStartsWaiting()
    {
        var budget = new ConnectionLifetimeBudget(1, 1);
        var prewarmStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrewarm = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var warmingPool = new ConnectionPool<TrackedConnection>(
            1,
            async cancellationToken =>
            {
                prewarmStarted.TrySetResult();
                await releasePrewarm.Task.WaitAsync(cancellationToken);
                return new TrackedConnection(1, () => { });
            },
            connectionBudget: budget);
        await using var foregroundPool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(2, () => { })),
            connectionBudget: budget);

        var warming = warmingPool.PrewarmAsync(1);
        await prewarmStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var foreground = foregroundPool.GetConnectionLockAsync(SemaphorePriority.Low);
        await Task.Delay(20);
        Assert.False(foreground.IsCompleted);

        releasePrewarm.TrySetResult();
        await warming.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, warmingPool.LiveConnections);
        Assert.False(foreground.IsCompleted);

        warmingPool.CloseIdleConnections();
        using var acquired = await foreground.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, foregroundPool.LiveConnections);
    }

    [Fact]
    public async Task PrewarmPublishesConnectionsBeforeTheFullTargetIsReady()
    {
        var attempts = 0;
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemaining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TrackedConnection>(
            4,
            async ct =>
            {
                var id = Interlocked.Increment(ref attempts);
                if (id == 1)
                {
                    firstPublished.TrySetResult();
                    return new TrackedConnection(id, () => { });
                }

                await releaseRemaining.Task.WaitAsync(ct);
                return new TrackedConnection(id, () => { });
            });

        var warming = pool.PrewarmAsync(4);
        await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using var available = await pool.GetConnectionLockAsync(SemaphorePriority.Low)
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(warming.IsCompleted);
        Assert.Equal(1, available.Connection.Id);

        releaseRemaining.TrySetResult();
        await warming.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WarmRefreshTimerReplacesDeadIdleConnections()
    {
        var created = 0;
        var disposed = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            4,
            _ => ValueTask.FromResult(new TrackedConnection(
                Interlocked.Increment(ref created), () => Interlocked.Increment(ref disposed))),
            connectionValidator: (connection, _) => ValueTask.FromResult(connection.Healthy),
            minimumIdleConnections: 2,
            warmConnectionRefreshInterval: TimeSpan.FromMilliseconds(20));

        await pool.PrewarmAsync(2);
        var stale = new List<TrackedConnection>();
        using (var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        using (var second = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        {
            stale.Add(first.Connection);
            stale.Add(second.Connection);
        }
        foreach (var connection in stale) connection.Healthy = false;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (Volatile.Read(ref created) < 4)
            await Task.Delay(10, timeout.Token);

        Assert.Equal(2, disposed);
        Assert.Equal(2, pool.LiveConnections);
        Assert.Equal(2, pool.IdleConnections);
    }

    [Fact]
    public async Task WarmRefreshTimerRetriesPrewarmWithoutQueuingMaintenanceAcquisitions()
    {
        var budget = new ConnectionLifetimeBudget(2, 2);
        await using var holder = new ConnectionPool<TrackedConnection>(
            2,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => { })),
            connectionBudget: budget);
        await using var warming = new ConnectionPool<TrackedConnection>(
            2,
            _ => ValueTask.FromResult(new TrackedConnection(2, () => { })),
            minimumIdleConnections: 2,
            warmConnectionRefreshInterval: TimeSpan.FromMilliseconds(20),
            connectionBudget: budget);
        var maximumPending = 0;
        warming.OnConnectionPoolChanged += (_, args) =>
            UpdateMaximum(ref maximumPending, args.Pending);

        await holder.PrewarmAsync(2);
        await warming.PrewarmAsync(2);
        await Task.Delay(60);

        Assert.Equal(0, warming.LiveConnections);
        Assert.Equal(0, Volatile.Read(ref maximumPending));

        holder.CloseIdleConnections();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (warming.LiveConnections < 2)
            await Task.Delay(10, timeout.Token);

        Assert.Equal(2, warming.IdleConnections);
        Assert.Equal(0, Volatile.Read(ref maximumPending));
    }

    [Fact]
    public async Task WarmRefreshValidatesOnlyABoundedMaintenanceBatch()
    {
        var validated = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            20,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => { })),
            connectionValidator: (_, _) =>
            {
                Interlocked.Increment(ref validated);
                return ValueTask.FromResult(true);
            },
            minimumIdleConnections: 20);

        await pool.PrewarmAsync(20);
        await pool.RefreshWarmConnectionsAsync();

        Assert.Equal(4, Volatile.Read(ref validated));
        Assert.Equal(20, pool.LiveConnections);
    }

    [Fact]
    public async Task SharedBudgetPrewarmUsesOnlyImmediatelyAvailableSlots()
    {
        var budget = new ConnectionLifetimeBudget(2, 2);
        await using var firstPool = new ConnectionPool<TrackedConnection>(
            2,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => { })),
            connectionBudget: budget);
        await using var secondPool = new ConnectionPool<TrackedConnection>(
            2,
            _ => ValueTask.FromResult(new TrackedConnection(2, () => { })),
            connectionBudget: budget);

        await firstPool.PrewarmAsync(2);
        await secondPool.PrewarmAsync(1).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, firstPool.LiveConnections);
        Assert.Equal(0, secondPool.LiveConnections);

        firstPool.CloseIdleConnections();
        await secondPool.PrewarmAsync(1).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, firstPool.LiveConnections);
        Assert.Equal(1, secondPool.LiveConnections);
    }

    [Fact]
    public async Task SharedBudgetCapsConcurrentHandshakesAcrossPools()
    {
        var budget = new ConnectionLifetimeBudget(4, 1);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maxActive = 0;
        ValueTask<TrackedConnection> Factory(CancellationToken cancellationToken) => Create(cancellationToken);

        async ValueTask<TrackedConnection> Create(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref started);
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, current);
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return new TrackedConnection(1, () => { });
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        await using var firstPool = new ConnectionPool<TrackedConnection>(
            2, Factory, connectionBudget: budget);
        await using var secondPool = new ConnectionPool<TrackedConnection>(
            2, Factory, connectionBudget: budget);

        var acquisitions = new[]
        {
            firstPool.GetConnectionLockAsync(SemaphorePriority.Low),
            firstPool.GetConnectionLockAsync(SemaphorePriority.Low),
            secondPool.GetConnectionLockAsync(SemaphorePriority.Low),
            secondPool.GetConnectionLockAsync(SemaphorePriority.Low)
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (Volatile.Read(ref started) == 0)
            await Task.Delay(5, timeout.Token);
        await Task.Delay(30, timeout.Token);

        Assert.Equal(1, Volatile.Read(ref started));
        release.TrySetResult();
        var leases = await Task.WhenAll(acquisitions).WaitAsync(timeout.Token);
        Assert.Equal(1, Volatile.Read(ref maxActive));
        foreach (var lease in leases) lease.Dispose();
    }

    [Fact]
    public async Task PrewarmDoesNotTakeCapacityAheadOfWaitingForegroundAcquisition()
    {
        var budget = new ConnectionLifetimeBudget(1, 1);
        await using var holderPool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => { })),
            connectionBudget: budget);
        await using var foregroundPool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(2, () => { })),
            connectionBudget: budget);
        await using var backgroundPool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(3, () => { })),
            connectionBudget: budget);

        await holderPool.PrewarmAsync(1);
        var foreground = foregroundPool.GetConnectionLockAsync(SemaphorePriority.Low);
        await backgroundPool.PrewarmAsync(1).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(foreground.IsCompleted);
        Assert.Equal(0, backgroundPool.LiveConnections);

        holderPool.CloseIdleConnections();
        using var lease = await foreground.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, lease.Connection.Id);
        Assert.Equal(0, backgroundPool.LiveConnections);
    }

    [Fact]
    public async Task SharedBudgetWaitersReuseReturnedIdleConnection()
    {
        var budget = new ConnectionLifetimeBudget(1, 1);
        var twoPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TrackedConnection>(
            3,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => { })),
            connectionBudget: budget);
        pool.OnConnectionPoolChanged += (_, args) =>
        {
            if (args.Pending >= 2) twoPending.TrySetResult();
        };

        var borrowed = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var firstWaiter = pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var secondWaiter = pool.GetConnectionLockAsync(SemaphorePriority.Low);
        await twoPending.Task.WaitAsync(TimeSpan.FromSeconds(1));

        borrowed.Dispose();
        var firstReused = await Task.WhenAny(firstWaiter, secondWaiter)
            .WaitAsync(TimeSpan.FromSeconds(1));
        using (var lease = await firstReused)
            Assert.Equal(1, lease.Connection.Id);

        var remainingWaiter = firstReused == firstWaiter ? secondWaiter : firstWaiter;
        using var secondReused = await remainingWaiter.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, secondReused.Connection.Id);
        Assert.Equal(1, pool.LiveConnections);
    }

    [Fact]
    public async Task ReturnedIdleConnectionKeepsManyCreationWaitersMoving()
    {
        var releaseCreations = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            32,
            async cancellationToken =>
            {
                var id = Interlocked.Increment(ref created);
                if (id > 1) await releaseCreations.Task.WaitAsync(cancellationToken);
                return new TrackedConnection(id, () => { });
            });

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var completed = 0;
        var waiters = Enumerable.Range(0, 24)
            .Select(async _ =>
            {
                using var lease = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
                Interlocked.Increment(ref completed);
            })
            .ToArray();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (Volatile.Read(ref created) < 2)
            await Task.Delay(5, timeout.Token);

        first.Dispose();
        while (Volatile.Read(ref completed) < waiters.Length)
            await Task.Delay(5, timeout.Token);

        Assert.Equal(waiters.Length, Volatile.Read(ref completed));
        Assert.Equal(1, pool.LiveConnections);
        releaseCreations.TrySetResult();
        await Task.WhenAll(waiters);
    }

    [Fact]
    public async Task ReplacementWaitingOnSharedBudgetYieldsToReturnedIdleConnection()
    {
        var budget = new ConnectionLifetimeBudget(2, 2);
        var created = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            2,
            _ => ValueTask.FromResult(new TrackedConnection(
                Interlocked.Increment(ref created), () => { })),
            connectionValidator: (connection, _) => ValueTask.FromResult(connection.Healthy),
            validateAfterIdle: TimeSpan.Zero,
            connectionBudget: budget);
        var held = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var unhealthy = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        unhealthy.Connection.Healthy = false;
        unhealthy.Dispose();

        var competingReservationTask = budget.ReserveCreationAsync(CancellationToken.None).AsTask();
        await Task.Yield();
        var replacement = pool.GetConnectionLockAsync(SemaphorePriority.Low);
        using var competing = await competingReservationTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(replacement.IsCompleted);

        var heldId = held.Connection.Id;
        held.Dispose();
        using var reused = await replacement.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(heldId, reused.Connection.Id);
        Assert.Equal(1, pool.LiveConnections);
        Assert.Equal(2, Volatile.Read(ref created));
    }

    [Fact]
    public async Task SynchronousDisposeCancelsAndDrainsActivePrewarm()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new ConnectionPool<TrackedConnection>(
            1,
            async cancellationToken =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Factory should have been cancelled.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    throw;
                }
            });

        var warming = pool.PrewarmAsync(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        pool.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);
    }

    [Fact]
    public async Task CloseIdleConnectionsReleasesCachedProviderSlots()
    {
        var disposed = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => Interlocked.Increment(ref disposed))));

        using (await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        {
        }

        pool.CloseIdleConnections();
        Assert.Equal(1, disposed);
        Assert.Equal(0, pool.LiveConnections);
        Assert.Equal(0, pool.IdleConnections);
    }

    [Fact]
    public async Task ColdPoolLimitsConcurrentConnectionCreationWithoutReducingCapacity()
    {
        var releaseFactories = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedFactories = 0;
        var activeFactories = 0;
        var maxActiveFactories = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var pool = new ConnectionPool<TrackedConnection>(
            64,
            async ct =>
            {
                var active = Interlocked.Increment(ref activeFactories);
                var started = Interlocked.Increment(ref startedFactories);
                UpdateMaximum(ref maxActiveFactories, active);
                try
                {
                    await releaseFactories.Task.WaitAsync(ct);
                    return new TrackedConnection(started, () => { });
                }
                finally
                {
                    Interlocked.Decrement(ref activeFactories);
                }
            });

        var leases = Enumerable.Range(0, 64)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.Low, timeout.Token))
            .ToArray();

        while (Volatile.Read(ref startedFactories) < 16 && !timeout.IsCancellationRequested)
            await Task.Delay(10, timeout.Token);

        Assert.Equal(16, Volatile.Read(ref maxActiveFactories));
        releaseFactories.TrySetResult();
        var acquired = await Task.WhenAll(leases);
        Assert.Equal(64, pool.LiveConnections);
        foreach (var lease in acquired) lease.Dispose();
    }

    [Fact]
    public async Task CancelledHandshakeWaiterDoesNotLeakProviderCapacity()
    {
        var releaseFactories = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedFactories = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            17,
            async ct =>
            {
                var id = Interlocked.Increment(ref startedFactories);
                if (id <= 16) await releaseFactories.Task.WaitAsync(ct);
                return new TrackedConnection(id, () => { });
            });

        var firstWave = Enumerable.Range(0, 16)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.Low))
            .ToArray();
        using var setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (Volatile.Read(ref startedFactories) < 16)
            await Task.Delay(5, setupTimeout.Token);

        using var cancelledCts = new CancellationTokenSource();
        var cancelledWaiter = pool.GetConnectionLockAsync(SemaphorePriority.Low, cancelledCts.Token);
        await Task.Delay(20, setupTimeout.Token);
        await cancelledCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);

        releaseFactories.TrySetResult();
        var firstLeases = await Task.WhenAll(firstWave);
        foreach (var lease in firstLeases) lease.Dispose();

        using var capacityTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var fullCapacity = Enumerable.Range(0, 17)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.Low, capacityTimeout.Token))
            .ToArray();
        var acquired = await Task.WhenAll(fullCapacity);
        Assert.Equal(17, pool.LiveConnections);
        foreach (var lease in acquired) lease.Dispose();
    }

    [Fact]
    public async Task ConcurrentLimitRejectionsDoNotDisableColdPoolWithAcceptedHandshake()
    {
        var allFactoriesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAcceptedFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var reducedTo = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            8,
            async _ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 8) allFactoriesStarted.TrySetResult();
                await allFactoriesStarted.Task;
                if (attempt != 1) throw new TestConnectionLimitException();
                await releaseAcceptedFactory.Task;
                return new TrackedConnection(attempt, () => { });
            },
            connectionCapacityRejected: e => e is TestConnectionLimitException,
            onConnectionCapacityReduced: (capacity, _) => reducedTo = capacity);

        var acquisitions = Enumerable.Range(0, 8)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.Low))
            .ToArray();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await allFactoriesStarted.Task.WaitAsync(timeout.Token);
        while (acquisitions.Count(x => x.IsCompleted) < 7)
            await Task.Delay(5, timeout.Token);

        var rejected = acquisitions.Where(x => x.IsCompleted).ToArray();
        Assert.Equal(7, rejected.Length);
        await Task.WhenAll(rejected.Select(async acquisition =>
            await Assert.ThrowsAsync<TestConnectionLimitException>(() => acquisition)));

        Assert.False(pool.CapacityUnavailable);
        Assert.Equal(1, reducedTo);

        var acceptedAcquisition = acquisitions.Single(x => !x.IsCompleted);
        releaseAcceptedFactory.TrySetResult();
        using var accepted = await acceptedAcquisition.WaitAsync(timeout.Token);
        Assert.Equal(1, pool.LiveConnections);
    }

    [Fact]
    public async Task PrewarmCreatesRequestedIdleConnections()
    {
        var created = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            8,
            _ => ValueTask.FromResult(new TrackedConnection(
                Interlocked.Increment(ref created), () => { })),
            minimumIdleConnections: 3);

        await pool.PrewarmAsync(3);

        Assert.Equal(3, created);
        Assert.Equal(3, pool.LiveConnections);
        Assert.Equal(3, pool.IdleConnections);
    }

    [Fact]
    public async Task ProviderLimitReducesCapacityAndReusesAcceptedConnections()
    {
        var attempts = 0;
        var reducedTo = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            4,
            _ =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt > 2) throw new TestConnectionLimitException();
                return ValueTask.FromResult(new TrackedConnection(attempt, () => { }));
            },
            connectionCapacityRejected: e => e is TestConnectionLimitException,
            onConnectionCapacityReduced: (capacity, _) => reducedTo = capacity);

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var firstId = first.Connection.Id;
        using var second = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        await Assert.ThrowsAsync<TestConnectionLimitException>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.Low));
        Assert.Equal(2, reducedTo);

        var waiting = pool.GetConnectionLockAsync(SemaphorePriority.Low);
        await Task.Delay(20);
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var reused = await waiting.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(3, attempts);
        Assert.Equal(firstId, reused.Connection.Id);
    }

    [Fact]
    public async Task ProviderLimitWithNoAcceptedConnectionsFailsAllWaiters()
    {
        await using var pool = new ConnectionPool<TrackedConnection>(
            8,
            _ => throw new TestConnectionLimitException(),
            connectionCapacityRejected: e => e is TestConnectionLimitException);

        var acquisitions = Enumerable.Range(0, 8)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.Low))
            .ToArray();

        await Task.WhenAll(acquisitions.Select(async acquisition =>
            await Assert.ThrowsAnyAsync<Exception>(() => acquisition)))
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(pool.CapacityUnavailable);
        Assert.Equal(0, pool.AvailableConnections);
        await Assert.ThrowsAnyAsync<Exception>(
            () => pool.GetConnectionLockAsync(SemaphorePriority.Low)).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreationWaitersReuseConnectionsReturnedDuringLaterHandshakes()
    {
        var attempts = 0;
        var releaseLaterHandshakes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var pool = new ConnectionPool<TrackedConnection>(
            33,
            async ct =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt > 16) await releaseLaterHandshakes.Task.WaitAsync(ct);
                return new TrackedConnection(attempt, () => { });
            });

        var acquisitions = Enumerable.Range(0, 33)
            .Select(_ => pool.GetConnectionLockAsync(SemaphorePriority.Low, timeout.Token))
            .ToArray();

        while (Volatile.Read(ref attempts) < 32)
            await Task.Delay(5, timeout.Token);
        var completedBefore = acquisitions.Where(x => x.IsCompletedSuccessfully).ToHashSet();
        Assert.Equal(16, completedBefore.Count);
        var returnedTask = completedBefore.First();
        var returned = await returnedTask;
        var returnedId = returned.Connection.Id;
        returned.Dispose();

        while (acquisitions.Count(x => x.IsCompletedSuccessfully) == completedBefore.Count)
            await Task.Delay(5, timeout.Token);
        var reusedTask = acquisitions.First(x => x.IsCompletedSuccessfully && !completedBefore.Contains(x));
        var reused = await reusedTask;
        Assert.Equal(returnedId, reused.Connection.Id);
        Assert.Equal(32, Volatile.Read(ref attempts));

        reused.Dispose();
        releaseLaterHandshakes.TrySetResult();
        var all = await Task.WhenAll(acquisitions);
        for (var i = 0; i < acquisitions.Length; i++)
            if (acquisitions[i] != returnedTask && acquisitions[i] != reusedTask)
                all[i].Dispose();
    }

    [Fact]
    public async Task InFlightSpeculativeHandshakeYieldsToReturnedIdleConnection()
    {
        var attempts = 0;
        var speculativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var speculativeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var pool = new ConnectionPool<TrackedConnection>(
            2,
            async ct =>
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (attempt == 1) return new TrackedConnection(attempt, () => { });

                speculativeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    throw new InvalidOperationException("The speculative handshake should be cancelled.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    speculativeCancelled.TrySetResult();
                    throw;
                }
            });

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low, timeout.Token);
        var firstId = first.Connection.Id;
        var waiting = pool.GetConnectionLockAsync(SemaphorePriority.Low, timeout.Token);
        await speculativeStarted.Task.WaitAsync(timeout.Token);

        first.Dispose();
        using var reused = await waiting.WaitAsync(timeout.Token);

        Assert.Equal(firstId, reused.Connection.Id);
        await speculativeCancelled.Task.WaitAsync(timeout.Token);
        Assert.Equal(2, attempts);
        Assert.Equal(1, pool.LiveConnections);
    }

    [Fact]
    public async Task SlowHandshakeCancellationDoesNotDelayReturnedIdleConnection()
    {
        var budget = new ConnectionLifetimeBudget(2, 2);
        var attempt = 0;
        var speculativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new ConnectionPool<TrackedConnection>(
            2,
            async ct =>
            {
                var id = Interlocked.Increment(ref attempt);
                if (id == 1) return new TrackedConnection(id, () => { });

                speculativeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    throw new InvalidOperationException("The speculative handshake should be cancelled.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await releaseCancellationCleanup.Task;
                    throw;
                }
            },
            connectionBudget: budget);
        await using var competingPool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(100, () => { })),
            connectionBudget: budget);

        var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        var firstId = first.Connection.Id;
        var waiting = pool.GetConnectionLockAsync(SemaphorePriority.Low);
        await speculativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        first.Dispose();
        using var reused = await waiting.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.Equal(firstId, reused.Connection.Id);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // The abandoned handshake still owns its lifetime reservation until its
        // cancellation cleanup finishes, preventing an unbounded replacement burst.
        // Pool disposal also tracks that cleanup instead of leaving an orphaned task.
        var disposing = pool.DisposeAsync().AsTask();
        await competingPool.PrewarmAsync(1).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(disposing.IsCompleted);
        Assert.Equal(0, competingPool.LiveConnections);

        releaseCancellationCleanup.TrySetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(1));
        await competingPool.PrewarmAsync(1).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, competingPool.LiveConnections);
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

    private sealed class TrackedConnection(int id, Action onDispose) : IDisposable
    {
        public int Id { get; } = id;
        public bool Healthy { get; set; } = true;
        public void Dispose() => onDispose();
    }

    private sealed class TestConnectionLimitException : Exception
    {
    }
}
