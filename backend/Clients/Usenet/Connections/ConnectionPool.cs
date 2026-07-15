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
    private readonly SemaphoreSlim _idleSignal = new(0, 1);
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly CancellationTokenSource _capacityCts = new();
    private readonly Task _sweeperTask; // keeps timer alive
    private TaskCompletionSource _operationsDrained = CompletedTaskSource();

    private int _live; // number of connections currently alive
    private int _inFlightCreations;
    private int _pendingAcquisitions;
    private int _effectiveMaxConnections;
    private Exception? _capacityUnavailableException;
    private int _capacityUnavailable;
    private int _disposed; // 0 == false, 1 == true
    private int _activeOperations;

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
        _creationGate = new SemaphoreSlim(Math.Min(maxConnections, 8), Math.Min(maxConnections, 8));
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
                        if (replaceDiscardedConnection)
                        {
                            conn = await CreateConnectionAsync(linked.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            var speculative = await CreateUnlessIdleReturnsAsync(linked.Token).ConfigureAwait(false);
                            if (!speculative.Created)
                            {
                                ReleaseConnectionCreationReservation();
                                creationReservationHeld = false;
                                if (speculative.ReusedIdle)
                                {
                                    conn = speculative.Connection;
                                    break;
                                }

                                replaceDiscardedConnection = false;
                                continue;
                            }

                            conn = speculative.Connection;
                        }

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
        lock (_idleLock)
        {
            if (Volatile.Read(ref _disposed) == 1)
                destroy = true;
            else
            {
                _idleConnections.Push(new Pooled(connection, Environment.TickCount64));
                _gate.Release();
            }
        }

        if (destroy)
        {
            DiscardConnection(connection);
            return;
        }

        SignalIdleAvailable();
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

    private static TaskCompletionSource CompletedTaskSource()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
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

    private async Task<(bool Created, bool ReusedIdle, T Connection)> CreateUnlessIdleReturnsAsync(
        CancellationToken cancellationToken)
    {
        using var creationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var creationTask = CreateConnectionAsync(creationCts.Token).AsTask();
        var idleTask = _idleSignal.WaitAsync(idleCts.Token);
        var completed = await Task.WhenAny(creationTask, idleTask).ConfigureAwait(false);

        if (completed == creationTask)
        {
            idleCts.Cancel();
            try
            {
                await idleTask.ConfigureAwait(false);
                SignalIdleAvailable();
            }
            catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
            {
            }

            return (true, false, await creationTask.ConfigureAwait(false));
        }

        await idleTask.ConfigureAwait(false);
        if (!IdleConnectionsCanSatisfyPendingAcquisitions())
        {
            // Under sustained load the pool still needs to grow. Preserve this
            // handshake and pass the idle notification to another waiter.
            SignalIdleAvailable();
            return (true, false, await creationTask.ConfigureAwait(false));
        }

        creationCts.Cancel();
        try
        {
            // The handshake may have won the race just after the idle signal.
            var connection = await creationTask.ConfigureAwait(false);
            SignalIdleAvailable();
            return (true, false, connection);
        }
        catch (OperationCanceledException) when (
            creationCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return TryTakeIdle(out var idle)
                ? (false, true, idle.Connection)
                : (false, false, default!);
        }
    }

    private bool IdleConnectionsCanSatisfyPendingAcquisitions()
    {
        lock (_idleLock)
            return _idleConnections.Count >= Math.Max(1, Volatile.Read(ref _pendingAcquisitions));
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
        bool taken;
        lock (_idleLock)
            taken = _idleConnections.TryPop(out item);
        if (taken && !_idleConnections.IsEmpty) SignalIdleAvailable();
        return taken;
    }

    private async Task<bool> WaitForCreationSlotOrIdleAsync(CancellationToken cancellationToken)
    {
        using var creationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var creationTask = _creationGate.WaitAsync(creationCts.Token);
        var idleTask = _idleSignal.WaitAsync(idleCts.Token);
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
            SignalIdleAvailable();
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
        {
        }
        return true;
    }

    private void SignalIdleAvailable()
    {
        if (_idleConnections.IsEmpty || _idleSignal.CurrentCount > 0) return;
        try
        {
            _idleSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
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
        List<T> idle;
        lock (_idleLock)
        {
            idle = new List<T>(_idleConnections.Count);
            while (_idleConnections.TryPop(out var item))
                idle.Add(item.Connection);
        }

        foreach (var connection in idle)
            DiscardConnection(connection);
    }

    public async Task PrewarmAsync(int count, CancellationToken cancellationToken = default)
    {
        BeginOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _sweepCts.Token, _capacityCts.Token);
            await _maintenanceGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var target = Math.Clamp(count, 0, _maxConnections);
                var connectionsNeeded = Math.Max(
                    0, target - LiveConnections - Volatile.Read(ref _inFlightCreations));
                if (connectionsNeeded == 0) return;

                var workerCount = Math.Min(connectionsNeeded, Math.Min(_maxConnections, 8));
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

        BeginOperation();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _sweepCts.Token, _capacityCts.Token);
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

            T connection;
            try
            {
                connection = await CreateConnectionAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (_connectionCapacityRejected?.Invoke(e) == true)
                    ReduceConnectionCapacity(e);
                throw;
            }

            if (!TryPublishPrewarmedConnection(connection))
            {
                DisposeConnection(connection);
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
            if (Volatile.Read(ref _disposed) == 1 || CapacityUnavailable) return false;
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
            if (_warmConnectionRefreshInterval.HasValue && _warmConnectionRefreshInterval.Value < interval)
                interval = _warmConnectionRefreshInterval.Value;
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
                        await RefreshWarmConnectionsAsync(_sweepCts.Token).ConfigureAwait(false);
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
        lock (_idleLock)
        {
            if (_disposed == 1) return;
            _disposed = 1;
            operationsDrained = _operationsDrained.Task;
        }

        await _sweepCts.CancelAsync();

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
        _idleSignal.Dispose();
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
}
