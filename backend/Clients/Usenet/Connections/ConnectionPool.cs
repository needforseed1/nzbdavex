using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Thread-safe, lazy connection pool.
/// <para>
/// *  Connections are created through a user-supplied factory (sync or async).<br/>
/// *  At most <c>maxConnections</c> live instances exist at any time.<br/>
/// *  Idle connections older than <see cref="IdleTimeout"/> are disposed
///    automatically by a background sweeper.<br/>
/// *  <see cref="Dispose"/> / <see cref="DisposeAsync"/> stop the sweeper and
///    dispose all cached connections.  Borrowed handles returned afterwards are
///    destroyed immediately.
/// *  Note: This class was authored by ChatGPT 3o
/// </para>
/// </summary>
public sealed class ConnectionPool<T> : IDisposable, IAsyncDisposable
{
    private const int WarmValidationBatchSize = 4;
    private const int ConcurrentConnectionCreationLimit = 16;
    private static readonly TimeSpan UnderfilledWarmRetryInterval = TimeSpan.FromSeconds(2);

    /* -------------------------------- configuration -------------------------------- */

    public TimeSpan IdleTimeout { get; }
    public int LiveConnections => _live;
    public int IdleConnections => _idleConnections.Count;
    public int ActiveConnections => _live - _idleConnections.Count;
    public int MaxConnections => _maxConnections;
    public int AvailableConnections => Math.Max(0, _effectiveMaxConnections - ActiveConnections);
    public bool CapacityUnavailable => Volatile.Read(ref _capacityUnavailable) == 1;

    public event EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs>? OnConnectionPoolChanged;

    private readonly Func<CancellationToken, ValueTask<T>> _factory;
    private readonly Func<T, CancellationToken, ValueTask<bool>>? _validator;
    private readonly TimeSpan _validateAfterIdle;
    private readonly TimeSpan? _warmConnectionRefreshInterval;
    private readonly int _maxConnections;
    private readonly int _minimumIdleConnections;
    private readonly Func<Exception, bool>? _connectionCapacityRejected;
    private readonly Action<int, Exception>? _onConnectionCapacityReduced;
    private readonly ConnectionLifetimeBudget? _connectionBudget;

    /* --------------------------------- state --------------------------------------- */

    private readonly ConcurrentStack<Pooled> _idleConnections = new();
    private readonly object _idleLock = new();
    private readonly PrioritizedSemaphore _gate;
    private readonly SemaphoreSlim _creationGate;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly CancellationTokenSource _capacityCts = new();
    private MaintenanceWorkGeneration _maintenanceWork = new();
    private readonly Task _sweeperTask; // keeps timer alive
    private TaskCompletionSource _operationsDrained = CompletedTaskSource();
    private readonly Queue<TaskCompletionSource> _idleWaiters = new();
    private TaskCompletionSource? _nextConnectionReturn;
    private readonly Dictionary<long, int> _prewarmSuspensions = [];

    private int _live; // number of connections currently alive
    private int _inFlightCreations;
    private int _pendingAcquisitions;
    private int _effectiveMaxConnections;
    private Exception? _capacityUnavailableException;
    private int _capacityUnavailable;
    private int _disposed; // 0 == false, 1 == true
    private int _activeOperations;
    private long _nextPrewarmSuspensionId;

    /* ------------------------------------------------------------------------------ */

    public ConnectionPool(
        int maxConnections,
        Func<CancellationToken, ValueTask<T>> connectionFactory,
        TimeSpan? idleTimeout = null,
        Func<T, CancellationToken, ValueTask<bool>>? connectionValidator = null,
        TimeSpan? validateAfterIdle = null,
        int minimumIdleConnections = 0,
        TimeSpan? warmConnectionRefreshInterval = null,
        Func<Exception, bool>? connectionCapacityRejected = null,
        Action<int, Exception>? onConnectionCapacityReduced = null,
        ConnectionLifetimeBudget? connectionBudget = null)
    {
        if (maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (minimumIdleConnections < 0 || minimumIdleConnections > maxConnections)
            throw new ArgumentOutOfRangeException(nameof(minimumIdleConnections));
        if (warmConnectionRefreshInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(warmConnectionRefreshInterval));

        _factory = connectionFactory
                   ?? throw new ArgumentNullException(nameof(connectionFactory));
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _validator = connectionValidator;
        _validateAfterIdle = validateAfterIdle ?? TimeSpan.FromSeconds(20);
        _minimumIdleConnections = minimumIdleConnections;
        _warmConnectionRefreshInterval = warmConnectionRefreshInterval;
        _connectionCapacityRejected = connectionCapacityRejected;
        _onConnectionCapacityReduced = onConnectionCapacityReduced;
        _connectionBudget = connectionBudget;

        _maxConnections = maxConnections;
        _effectiveMaxConnections = maxConnections;
        _gate = new PrioritizedSemaphore(maxConnections, maxConnections);
        _creationGate = new SemaphoreSlim(
            Math.Min(maxConnections, ConcurrentConnectionCreationLimit),
            Math.Min(maxConnections, ConcurrentConnectionCreationLimit));
        _sweeperTask = Task.Run(SweepLoop); // background idle-reaper
    }

    /* ============================== public API ==================================== */

    /// <summary>
    /// Borrow a connection while reserving capacity for higher-priority callers.
    /// Waits until at least (`reservedCount` + 1) slots are free before acquiring one,
    /// ensuring that after acquisition at least `reservedCount` remain available.
    /// </summary>
    public async Task<ConnectionLock<T>> GetConnectionLockAsync
    (
        SemaphorePriority priority,
        CancellationToken cancellationToken = default
    )
    {
        return await GetConnectionLockAsync(priority, cancellationToken, false).ConfigureAwait(false);
    }

    private async Task<ConnectionLock<T>> GetConnectionLockCoreAsync
    (
        SemaphorePriority priority,
        CancellationToken cancellationToken,
        bool forceIdleValidation
    )
    {
        // Make caller cancellation also cancel the wait on the gate.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sweepCts.Token, _capacityCts.Token);

        await _gate.WaitAsync(priority, linked.Token).ConfigureAwait(false);
        var returnGateOnFailure = true;

        try
        {
            // Pool might have been disposed after wait returned:
            if (Volatile.Read(ref _disposed) == 1)
            {
                ThrowDisposed();
            }

            T conn;
            var replaceDiscardedConnection = false;
            Task? replacementIdleTask = null;
            while (true)
            {
                // Try to reuse an existing idle connection.
                while (!replaceDiscardedConnection && TryTakeIdle(out var item))
                {
                    // A borrowed idle socket is active while it is being validated.
                    // Publish that transition before awaiting provider I/O.
                    TriggerConnectionPoolChangedEvent();
                    var idleExpired = item.IsExpired(IdleTimeout);
                    var preserveWarmFloor = idleExpired
                                            && _minimumIdleConnections > 0
                                            && _idleConnections.Count < _minimumIdleConnections;
                    if (idleExpired && !preserveWarmFloor)
                    {
                        replacementIdleTask = GetNextConnectionReturnTask();
                        DiscardConnection(item.Connection);
                        replaceDiscardedConnection = true;
                        break;
                    }

                    if (_validator != null &&
                        (forceIdleValidation || preserveWarmFloor || item.IsExpired(_validateAfterIdle)))
                    {
                        bool healthy;
                        try
                        {
                            healthy = await _validator(item.Connection, linked.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            DiscardConnection(item.Connection);
                            throw;
                        }

                        if (!healthy)
                        {
                            replacementIdleTask = GetNextConnectionReturnTask();
                            DiscardConnection(item.Connection);
                            replaceDiscardedConnection = true;
                            break;
                        }
                    }

                    TriggerConnectionPoolChangedEvent();
                    returnGateOnFailure = false;
                    return BuildLock(item.Connection);
                }

                // A returned socket and a free handshake slot are both valid ways
                // forward. Racing them prevents requests already queued for socket
                // creation from ignoring newly reusable connections.
                var ownsCreationSlot = replaceDiscardedConnection;
                if (replaceDiscardedConnection)
                    await _creationGate.WaitAsync(linked.Token).ConfigureAwait(false);
                else
                    ownsCreationSlot = await WaitForCreationSlotOrIdleAsync(linked.Token).ConfigureAwait(false);
                if (!ownsCreationSlot)
                {
                    replaceDiscardedConnection = false;
                    continue;
                }

                if (!replaceDiscardedConnection && !_idleConnections.IsEmpty)
                {
                    _creationGate.Release();
                    continue;
                }

                if (!TryReserveConnectionCreation())
                {
                    _creationGate.Release();
                    ReleaseGateIfOpen();
                    returnGateOnFailure = false;
                    return await GetConnectionLockCoreAsync(priority, cancellationToken, forceIdleValidation)
                        .ConfigureAwait(false);
                }

                // Need a fresh connection.
                var creationReservationHeld = true;
                try
                {
                    try
                    {
                        var speculative = await CreateUnlessIdleReturnsAsync(
                            linked.Token,
                            replaceDiscardedConnection ? replacementIdleTask : null).ConfigureAwait(false);
                        if (!speculative.Created)
                        {
                            ReleaseConnectionCreationReservation();
                            creationReservationHeld = false;
                            replaceDiscardedConnection = false;
                            replacementIdleTask = null;
                            continue;
                        }

                        conn = speculative.Connection;
                        RegisterCreatedConnection();
                        creationReservationHeld = false;
                    }
                    finally
                    {
                        _creationGate.Release();
                    }
                }
                catch (Exception e)
                {
                    if (creationReservationHeld) ReleaseConnectionCreationReservation();
                    if (_connectionCapacityRejected?.Invoke(e) == true)
                        ReduceConnectionCapacity(e);
                    throw;
                }

                break;
            }

            if (Volatile.Read(ref _disposed) == 1)
            {
                DisposeConnection(conn);
                Interlocked.Decrement(ref _live);
                ThrowDisposed();
            }

            if (CapacityUnavailable)
            {
                DisposeConnection(conn);
                Interlocked.Decrement(ref _live);
                ThrowCapacityUnavailable();
            }

            RaiseEffectiveCapacityToLive();
            TriggerConnectionPoolChangedEvent();
            returnGateOnFailure = false;
            return BuildLock(conn);

            ConnectionLock<T> BuildLock(T c)
                => new(c, Return, Destroy);

            static void ThrowDisposed()
                => throw new ObjectDisposedException(nameof(ConnectionPool<T>));
        }
        finally
        {
            if (returnGateOnFailure) ReleaseGateIfOpen();
        }
    }

    /* ========================== core helpers ====================================== */

    private readonly record struct Pooled(T Connection, long LastTouchedMillis)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsExpired(TimeSpan idle, long nowMillis = 0)
        {
            if (nowMillis == 0) nowMillis = Environment.TickCount64;
            return unchecked(nowMillis - LastTouchedMillis) >= idle.TotalMilliseconds;
        }
    }

    private void Return(T connection)
    {
        var destroy = false;
        TaskCompletionSource? idleAvailableSignal = null;
        TaskCompletionSource? returnedSignal = null;
        lock (_idleLock)
        {
            if (Volatile.Read(ref _disposed) == 1)
                destroy = true;
            else
            {
                _idleConnections.Push(new Pooled(connection, Environment.TickCount64));
                _gate.Release();
                idleAvailableSignal = TakeNextIdleWaiterUnsafe();
                returnedSignal = _nextConnectionReturn;
                _nextConnectionReturn = null;
            }
        }

        if (destroy)
        {
            Destroy(connection);
            return;
        }

        WakeOneIdleWaiter(idleAvailableSignal);
        returnedSignal?.TrySetResult();
        TriggerConnectionPoolChangedEvent();
    }

    private void Destroy(T connection)
    {
        // When a lock requests replacement, we dispose the connection instead of reusing.
        DisposeConnection(connection);
        Interlocked.Decrement(ref _live);
        ReleaseGateIfOpen();

        TriggerConnectionPoolChangedEvent();
    }

    private void TriggerConnectionPoolChangedEvent()
    {
        OnConnectionPoolChanged?.Invoke(this, new ConnectionPoolStats.ConnectionPoolChangedEventArgs(
            _live,
            _idleConnections.Count,
            _maxConnections,
            _pendingAcquisitions,
            _effectiveMaxConnections
        ));
    }

    private void ReduceConnectionCapacity(Exception exception)
    {
        int reducedTo;
        lock (_idleLock)
        {
            reducedTo = _live + _inFlightCreations;
            if (reducedTo >= _effectiveMaxConnections) return;
            _effectiveMaxConnections = reducedTo;
            if (reducedTo == 0)
            {
                _capacityUnavailableException = exception;
                Volatile.Write(ref _capacityUnavailable, 1);
                _gate.Dispose();
                _capacityCts.Cancel();
            }
            else
                _gate.UpdateMaxAllowed(reducedTo);
        }

        _onConnectionCapacityReduced?.Invoke(reducedTo, exception);
        TriggerConnectionPoolChangedEvent();
    }

    private bool TryReserveConnectionCreation()
    {
        lock (_idleLock)
        {
            if (Volatile.Read(ref _disposed) == 1 || CapacityUnavailable) return false;
            if (_live + _inFlightCreations >= _effectiveMaxConnections) return false;
            _inFlightCreations++;
            return true;
        }
    }

    private void BeginOperation()
    {
        lock (_idleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            if (_activeOperations++ == 0)
                _operationsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void EndOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_idleLock)
        {
            if (--_activeOperations == 0)
                drained = _operationsDrained;
        }

        drained?.TrySetResult();
    }

    private void BeginChildOperation()
    {
        lock (_idleLock)
        {
            // Child cleanup is registered while its parent acquisition is still
            // active, including when disposal has concurrently begun. This keeps
            // both operations on the same drain barrier without reopening a pool.
            if (_activeOperations <= 0)
                throw new InvalidOperationException(
                    "Cannot register a child operation without an active parent.");
            _activeOperations++;
        }
    }

    private static TaskCompletionSource CompletedTaskSource()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }

    private static TaskCompletionSource NewSignalTaskSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task GetNextConnectionReturnTask()
    {
        lock (_idleLock)
            return (_nextConnectionReturn ??= NewSignalTaskSource()).Task;
    }

    private void RegisterCreatedConnection()
    {
        lock (_idleLock)
        {
            _inFlightCreations--;
            Interlocked.Increment(ref _live);
        }
    }

    private void ReleaseConnectionCreationReservation()
    {
        lock (_idleLock)
            _inFlightCreations--;
    }

    private async ValueTask<T> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connectionBudget is null)
            return await _factory(cancellationToken).ConfigureAwait(false);

        using var reservation = await _connectionBudget
            .ReserveCreationAsync(cancellationToken).ConfigureAwait(false);
        var connection = await _factory(cancellationToken).ConfigureAwait(false);
        reservation.Commit();
        return connection;
    }

    private async ValueTask<(bool Created, T Connection)> TryCreatePrewarmedConnectionAsync(
        CancellationToken cancellationToken)
    {
        if (_connectionBudget is null)
            return (true, await _factory(cancellationToken).ConfigureAwait(false));

        if (!_connectionBudget.TryReserveCreation(out var reservation))
            return (false, default!);

        using (reservation)
        {
            var connection = await _factory(cancellationToken).ConfigureAwait(false);
            reservation.Commit();
            return (true, connection);
        }
    }

    private async Task<(bool Created, T Connection)> CreateUnlessIdleReturnsAsync(
        CancellationToken cancellationToken,
        Task? replacementIdleTask)
    {
        using var creationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var creationTask = CreateConnectionAsync(creationCts.Token).AsTask();
        var idleTask = replacementIdleTask is null
            ? WaitForIdleAsync(idleCts.Token)
            : replacementIdleTask.WaitAsync(idleCts.Token);
        var completed = await Task.WhenAny(creationTask, idleTask).ConfigureAwait(false);

        if (completed == creationTask)
        {
            idleCts.Cancel();
            try
            {
                await idleTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
            {
            }

            return (true, await creationTask.ConfigureAwait(false));
        }

        await idleTask.ConfigureAwait(false);
        // A reusable socket is always the shortest path forward. In particular,
        // creation may be waiting on the shared lifetime budget and cannot make
        // progress until another socket is destroyed. Let one returned socket
        // satisfy one waiter; later acquisitions can still grow the pool.
        creationCts.Cancel();

        // Cancellation of a TCP/TLS handshake can take noticeably longer than
        // signaling it. Do not keep an authenticated idle socket waiting for that
        // cleanup. The abandoned creation remains tracked as an active operation,
        // so pool disposal still drains it and the global lifetime budget remains
        // charged until it has either failed or produced a connection to dispose.
        if (!creationTask.IsCompleted)
        {
            BeginChildOperation();
            _ = DrainAbandonedCreationAsync(creationTask);
            return (false, default!);
        }

        try
        {
            // The handshake may have won the race just after the idle signal.
            var connection = await creationTask.ConfigureAwait(false);
            return (true, connection);
        }
        catch (OperationCanceledException) when (
            creationCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Re-enter the normal acquisition loop so the returned connection is
            // subject to the same expiry and health validation as any other idle
            // connection. Another waiter may win it first, which is harmless.
            return (false, default!);
        }
    }

    private async Task DrainAbandonedCreationAsync(Task<T> creationTask)
    {
        try
        {
            var connection = await creationTask.ConfigureAwait(false);
            DisposeConnection(connection);
        }
        catch (OperationCanceledException)
        {
            // Expected after an idle connection wins the acquisition race.
        }
        catch (Exception e)
        {
            // A provider connection-limit response still carries useful capacity
            // information even though this speculative connection is no longer
            // needed by its original borrower.
            if (_connectionCapacityRejected?.Invoke(e) == true)
                ReduceConnectionCapacity(e);
        }
        finally
        {
            EndOperation();
        }
    }

    private void RaiseEffectiveCapacityToLive()
    {
        lock (_idleLock)
        {
            if (_live <= _effectiveMaxConnections) return;
            _effectiveMaxConnections = Math.Min(_live, _maxConnections);
            _gate.UpdateMaxAllowed(_effectiveMaxConnections);
        }
    }

    private void ThrowCapacityUnavailable()
    {
        throw new InvalidOperationException(
            "The provider rejected all connection attempts for this pool.",
            _capacityUnavailableException);
    }

    private bool TryTakeIdle(out Pooled item)
    {
        lock (_idleLock)
            return _idleConnections.TryPop(out item);
    }

    private async Task<bool> WaitForCreationSlotOrIdleAsync(CancellationToken cancellationToken)
    {
        using var creationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var creationTask = _creationGate.WaitAsync(creationCts.Token);
        var idleTask = WaitForIdleAsync(idleCts.Token);
        var completed = await Task.WhenAny(creationTask, idleTask).ConfigureAwait(false);

        if (completed == idleTask)
        {
            await idleTask.ConfigureAwait(false);
            creationCts.Cancel();
            try
            {
                await creationTask.ConfigureAwait(false);
                _creationGate.Release();
            }
            catch (OperationCanceledException) when (creationCts.IsCancellationRequested)
            {
            }
            return false;
        }

        await creationTask.ConfigureAwait(false);
        idleCts.Cancel();
        try
        {
            await idleTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
        {
        }
        return true;
    }

    private async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource signal;
        lock (_idleLock)
        {
            if (!_idleConnections.IsEmpty) return;
            signal = NewSignalTaskSource();
            _idleWaiters.Enqueue(signal);
        }

        using var registration = cancellationToken.Register(
            () => signal.TrySetCanceled(cancellationToken));
        await signal.Task.ConfigureAwait(false);
    }

    private void SignalIdleAvailable()
    {
        WakeOneIdleWaiter();
    }

    private void WakeOneIdleWaiter(TaskCompletionSource? signal = null)
    {
        while (true)
        {
            if (signal is null)
            {
                lock (_idleLock)
                {
                    if (_idleConnections.IsEmpty) return;
                    signal = TakeNextIdleWaiterUnsafe();
                }
            }

            if (signal is null || signal.TrySetResult()) return;
            signal = null;
        }
    }

    private TaskCompletionSource? TakeNextIdleWaiterUnsafe()
    {
        while (_idleWaiters.TryDequeue(out var signal))
            if (!signal.Task.IsCompleted)
                return signal;
        return null;
    }

    private void CancelIdleWaiters()
    {
        List<TaskCompletionSource> waiters;
        lock (_idleLock)
        {
            waiters = [.. _idleWaiters];
            _idleWaiters.Clear();
        }

        foreach (var waiter in waiters)
            waiter.TrySetCanceled(_sweepCts.Token);
    }

    private void DiscardConnection(T connection)
    {
        DisposeConnection(connection);
        Interlocked.Decrement(ref _live);
        TriggerConnectionPoolChangedEvent();
    }

    private void ReleaseGateIfOpen()
    {
        lock (_idleLock)
        {
            if (Volatile.Read(ref _disposed) == 0 && !CapacityUnavailable)
                _gate.Release();
        }
    }

    public void CloseIdleConnections()
    {
        _ = TrimIdleConnections(0);
    }

    /// <summary>
    /// Temporarily stops speculative warming and retains at most the requested
    /// number of idle sockets. Borrowed connections and real acquisitions are
    /// never interrupted. Disposing the returned scope re-enables warming.
    /// </summary>
    public IDisposable SuspendPrewarming(int retainedIdleConnections, out int closedConnections)
    {
        retainedIdleConnections = Math.Clamp(retainedIdleConnections, 0, _maxConnections);
        MaintenanceWorkGeneration? maintenanceToRetire = null;
        long suspensionId;
        lock (_idleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            suspensionId = ++_nextPrewarmSuspensionId;
            if (_prewarmSuspensions.Count == 0)
                maintenanceToRetire = _maintenanceWork;
            _prewarmSuspensions.Add(suspensionId, retainedIdleConnections);
        }

        // Stop prewarm/validation already in progress before trimming. A
        // factory that completes after cancellation cannot publish above the
        // retained floor because TryPublishPrewarmedConnection checks the scope.
        maintenanceToRetire?.Retire();
        closedConnections = TrimIdleConnections(retainedIdleConnections);
        return new PrewarmSuspension(this, suspensionId);
    }

    private void ResumePrewarming(long suspensionId)
    {
        lock (_idleLock)
        {
            if (!_prewarmSuspensions.Remove(suspensionId) || _prewarmSuspensions.Count != 0)
                return;
            if (Volatile.Read(ref _disposed) == 1) return;
            _maintenanceWork = new MaintenanceWorkGeneration();
        }
    }

    private bool TryAcquireMaintenanceLease(out MaintenanceWorkGeneration.Lease lease)
    {
        lock (_idleLock)
        {
            if (_prewarmSuspensions.Count > 0 || Volatile.Read(ref _disposed) == 1)
            {
                lease = null!;
                return false;
            }

            lease = _maintenanceWork.Acquire();
            return true;
        }
    }

    public int TrimIdleConnections(int retainedIdleConnections)
    {
        retainedIdleConnections = Math.Clamp(retainedIdleConnections, 0, _maxConnections);
        List<T> idle;
        lock (_idleLock)
        {
            var retained = new List<Pooled>(retainedIdleConnections);
            idle = new List<T>(Math.Max(0, _idleConnections.Count - retainedIdleConnections));
            while (_idleConnections.TryPop(out var item))
            {
                if (retained.Count < retainedIdleConnections)
                    retained.Add(item);
                else
                    idle.Add(item.Connection);
            }

            // Preserve the newest retained connection at the top of the stack.
            for (var i = retained.Count - 1; i >= 0; i--)
                _idleConnections.Push(retained[i]);
        }

        foreach (var connection in idle)
            DiscardConnection(connection);

        return idle.Count;
    }

    public async Task PrewarmAsync(int count, CancellationToken cancellationToken = default)
    {
        if (!TryAcquireMaintenanceLease(out var maintenanceLease)) return;
        using (maintenanceLease)
        {
            await PrewarmWithLeaseAsync(count, cancellationToken, maintenanceLease.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task PrewarmWithLeaseAsync(
        int count,
        CancellationToken cancellationToken,
        CancellationToken maintenanceToken)
    {
        BeginOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _sweepCts.Token, _capacityCts.Token, maintenanceToken);
            await _maintenanceGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var target = Math.Clamp(count, 0, _maxConnections);
                var connectionsNeeded = Math.Max(
                    0, target - LiveConnections - Volatile.Read(ref _inFlightCreations));
                if (connectionsNeeded == 0) return;

                var workerCount = Math.Min(
                    connectionsNeeded,
                    Math.Min(_maxConnections, ConcurrentConnectionCreationLimit));
                var workers = Enumerable.Range(0, workerCount)
                    .Select(_ => PrewarmWorkerAsync(target, linked.Token))
                    .ToArray();
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            finally
            {
                _maintenanceGate.Release();
            }
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task RefreshWarmConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (_minimumIdleConnections == 0 || _validator == null) return;
        if (!TryAcquireMaintenanceLease(out var maintenanceLease)) return;
        using (maintenanceLease)
        {
            await RefreshWarmConnectionsWithLeaseAsync(cancellationToken, maintenanceLease.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task RefreshWarmConnectionsWithLeaseAsync(
        CancellationToken cancellationToken,
        CancellationToken maintenanceToken)
    {
        BeginOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _sweepCts.Token, _capacityCts.Token, maintenanceToken);
            await _maintenanceGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                // Keep maintenance cheap and staggered. Real borrowers still
                // validate any connection that exceeded validateAfterIdle.
                var count = Math.Min(
                    WarmValidationBatchSize,
                    Math.Min(_minimumIdleConnections, Volatile.Read(ref _effectiveMaxConnections)));
                await AcquireIdleConnectionsAsync(count, true, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _maintenanceGate.Release();
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PrewarmWorkerAsync(int target, CancellationToken cancellationToken)
    {
        while (await CreatePrewarmedConnectionAsync(target, cancellationToken).ConfigureAwait(false))
        {
        }
    }

    private async Task AcquireIdleConnectionsAsync(
        int count,
        bool forceIdleValidation,
        CancellationToken cancellationToken)
    {
        count = Math.Clamp(count, 0, _maxConnections);
        if (count == 0) return;

        var acquisitions = Enumerable.Range(0, count)
            .Select(_ => GetConnectionLockAsync(
                SemaphorePriority.Low, cancellationToken, forceIdleValidation))
            .ToArray();
        try
        {
            await Task.WhenAll(acquisitions).ConfigureAwait(false);
        }
        finally
        {
            foreach (var acquisition in acquisitions)
                if (acquisition.IsCompletedSuccessfully)
                    acquisition.Result.Dispose();
        }
    }

    private async Task<bool> CreatePrewarmedConnectionAsync(int target, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sweepCts.Token, _capacityCts.Token);
        await _creationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var reservationHeld = false;
        try
        {
            if (!TryReservePrewarmCreation(target)) return false;
            reservationHeld = true;

            (bool Created, T Connection) speculative;
            try
            {
                speculative = await TryCreatePrewarmedConnectionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (_connectionCapacityRejected?.Invoke(e) == true)
                    ReduceConnectionCapacity(e);
                throw;
            }

            if (!speculative.Created) return false;

            if (!TryPublishPrewarmedConnection(speculative.Connection))
            {
                DisposeConnection(speculative.Connection);
                return false;
            }

            reservationHeld = false;
            RaiseEffectiveCapacityToLive();
            SignalIdleAvailable();
            TriggerConnectionPoolChangedEvent();
            return true;
        }
        finally
        {
            if (reservationHeld) ReleaseConnectionCreationReservation();
            _creationGate.Release();
        }
    }

    private bool TryReservePrewarmCreation(int target)
    {
        lock (_idleLock)
        {
            if (Volatile.Read(ref _disposed) == 1 || CapacityUnavailable ||
                _prewarmSuspensions.Count > 0) return false;
            var effectiveTarget = Math.Min(target, _effectiveMaxConnections);
            if (_live + _inFlightCreations >= effectiveTarget) return false;
            _inFlightCreations++;
            return true;
        }
    }

    private bool TryPublishPrewarmedConnection(T connection)
    {
        lock (_idleLock)
        {
            if (Volatile.Read(ref _disposed) == 1 || CapacityUnavailable) return false;
            if (_prewarmSuspensions.Count > 0 &&
                _idleConnections.Count >= _prewarmSuspensions.Values.Min()) return false;
            _inFlightCreations--;
            Interlocked.Increment(ref _live);
            _idleConnections.Push(new Pooled(connection, Environment.TickCount64));
            return true;
        }
    }

    private async Task<ConnectionLock<T>> GetConnectionLockAsync(
        SemaphorePriority priority,
        CancellationToken cancellationToken,
        bool forceIdleValidation)
    {
        BeginOperation();
        Interlocked.Increment(ref _pendingAcquisitions);
        TriggerConnectionPoolChangedEvent();
        try
        {
            return await GetConnectionLockCoreAsync(priority, cancellationToken, forceIdleValidation)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            CapacityUnavailable &&
            !cancellationToken.IsCancellationRequested &&
            !_sweepCts.IsCancellationRequested)
        {
            ThrowCapacityUnavailable();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _pendingAcquisitions);
            TriggerConnectionPoolChangedEvent();
            EndOperation();
        }
    }

    /* =================== idle sweeper (background) ================================= */

    private async Task SweepLoop()
    {
        try
        {
            var interval = IdleTimeout / 2;
            if (_warmConnectionRefreshInterval.HasValue)
                interval = Min(interval, Min(
                    _warmConnectionRefreshInterval.Value,
                    UnderfilledWarmRetryInterval));
            var nextWarmRefreshAt = DateTimeOffset.UtcNow +
                                    (_warmConnectionRefreshInterval ?? TimeSpan.Zero);
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
            {
                SweepOnce();
                if (_warmConnectionRefreshInterval.HasValue &&
                    Volatile.Read(ref _pendingAcquisitions) == 0 &&
                    ActiveConnections == 0)
                {
                    try
                    {
                        // A startup or restore prewarm may have yielded to real
                        // work or another provider's handshakes. Retry missing
                        // warm capacity opportunistically while the pool is idle.
                        // Do not run forced validation while underfilled: that
                        // path could turn maintenance into queued acquisitions.
                        if (IdleConnections < _minimumIdleConnections)
                            await PrewarmAsync(_minimumIdleConnections, _sweepCts.Token)
                                .ConfigureAwait(false);
                        else if (DateTimeOffset.UtcNow >= nextWarmRefreshAt)
                        {
                            await RefreshWarmConnectionsAsync(_sweepCts.Token)
                                .ConfigureAwait(false);
                            nextWarmRefreshAt = DateTimeOffset.UtcNow +
                                                _warmConnectionRefreshInterval.Value;
                        }
                    }
                    catch (OperationCanceledException) when (_sweepCts.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // A later maintenance pass or real request will retry.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            /* normal on disposal */
        }
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private void SweepOnce()
    {
        var now = Environment.TickCount64;
        var expired = new List<T>();
        var isAnyConnectionFreed = false;

        lock (_idleLock)
        {
            var survivors = new List<Pooled>();
            var remainingExpirations = Math.Max(0, _idleConnections.Count - _minimumIdleConnections);
            while (_idleConnections.TryPop(out var item))
            {
                if (remainingExpirations > 0 && item.IsExpired(IdleTimeout, now))
                {
                    expired.Add(item.Connection);
                    remainingExpirations--;
                }
                else
                    survivors.Add(item);
            }

            // Preserve original LIFO order.
            for (var i = survivors.Count - 1; i >= 0; i--)
                _idleConnections.Push(survivors[i]);
        }

        foreach (var connection in expired)
        {
            DisposeConnection(connection);
            Interlocked.Decrement(ref _live);
            isAnyConnectionFreed = true;
        }

        if (isAnyConnectionFreed)
            TriggerConnectionPoolChangedEvent();
    }

    /* ------------------------- dispose helpers ------------------------------------ */

    private void DisposeConnection(T conn)
    {
        try
        {
            if (conn is IDisposable d)
                d.Dispose();
        }
        finally
        {
            _connectionBudget?.ReleaseConnection();
        }
    }

    /* -------------------------- IAsyncDisposable ---------------------------------- */

    public async ValueTask DisposeAsync()
    {
        Task operationsDrained;
        MaintenanceWorkGeneration maintenanceWork;
        lock (_idleLock)
        {
            if (_disposed == 1) return;
            _disposed = 1;
            operationsDrained = _operationsDrained.Task;
            maintenanceWork = _maintenanceWork;
        }

        await _sweepCts.CancelAsync();
        maintenanceWork.Retire();
        CancelIdleWaiters();

        try
        {
            await _sweeperTask.ConfigureAwait(false); // await clean sweep exit
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }

        // All acquisition, prewarm and validation work observes _sweepCts. Wait
        // for it to unwind before disposing the synchronization primitives it uses.
        await operationsDrained.ConfigureAwait(false);

        // Drain and dispose cached items.
        List<T> idle;
        lock (_idleLock)
        {
            idle = new List<T>(_idleConnections.Count);
            while (_idleConnections.TryPop(out var item))
                idle.Add(item.Connection);
        }

        foreach (var connection in idle)
        {
            DisposeConnection(connection);
            Interlocked.Decrement(ref _live);
        }

        _sweepCts.Dispose();
        _capacityCts.Dispose();
        _maintenanceGate.Dispose();
        _creationGate.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /* ----------------------------- IDisposable ------------------------------------ */

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class PrewarmSuspension(ConnectionPool<T> owner, long suspensionId) : IDisposable
    {
        private ConnectionPool<T>? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ResumePrewarming(suspensionId);
        }
    }

    private sealed class MaintenanceWorkGeneration
    {
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cancellation = new();
        private int _leases;
        private bool _retired;

        public Lease Acquire()
        {
            lock (_lock)
            {
                if (_retired)
                    throw new InvalidOperationException("Cannot acquire a retired maintenance generation.");
                _leases++;
                return new Lease(this, _cancellation.Token);
            }
        }

        public void Retire()
        {
            var dispose = false;
            lock (_lock)
            {
                if (_retired) return;
                _retired = true;
                _cancellation.Cancel();
                dispose = _leases == 0;
            }
            if (dispose) _cancellation.Dispose();
        }

        private void Release()
        {
            var dispose = false;
            lock (_lock)
            {
                _leases--;
                dispose = _retired && _leases == 0;
            }
            if (dispose) _cancellation.Dispose();
        }

        public sealed class Lease(
            MaintenanceWorkGeneration owner,
            CancellationToken token) : IDisposable
        {
            private MaintenanceWorkGeneration? _owner = owner;
            public CancellationToken Token { get; } = token;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release();
            }
        }
    }
}
