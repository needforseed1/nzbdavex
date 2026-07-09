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
    /* -------------------------------- configuration -------------------------------- */

    public TimeSpan IdleTimeout { get; }
    public int LiveConnections => _live;
    public int IdleConnections => _idleConnections.Count;
    public int ActiveConnections => _live - _idleConnections.Count;
    public int AvailableConnections => _maxConnections - ActiveConnections;

    public event EventHandler<ConnectionPoolStats.ConnectionPoolChangedEventArgs>? OnConnectionPoolChanged;

    private readonly Func<CancellationToken, ValueTask<T>> _factory;
    private readonly Func<T, CancellationToken, ValueTask<bool>>? _validator;
    private readonly TimeSpan _validateAfterIdle;
    private readonly int _maxConnections;

    /* --------------------------------- state --------------------------------------- */

    private readonly ConcurrentStack<Pooled> _idleConnections = new();
    private readonly object _idleLock = new();
    private readonly PrioritizedSemaphore _gate;
    private readonly CancellationTokenSource _sweepCts = new();
    private readonly Task _sweeperTask; // keeps timer alive

    private int _live; // number of connections currently alive
    private int _disposed; // 0 == false, 1 == true

    /* ------------------------------------------------------------------------------ */

    public ConnectionPool(
        int maxConnections,
        Func<CancellationToken, ValueTask<T>> connectionFactory,
        TimeSpan? idleTimeout = null,
        Func<T, CancellationToken, ValueTask<bool>>? connectionValidator = null,
        TimeSpan? validateAfterIdle = null)
    {
        if (maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));

        _factory = connectionFactory
                   ?? throw new ArgumentNullException(nameof(connectionFactory));
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _validator = connectionValidator;
        _validateAfterIdle = validateAfterIdle ?? TimeSpan.FromSeconds(20);

        _maxConnections = maxConnections;
        _gate = new PrioritizedSemaphore(maxConnections, maxConnections);
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
        // Make caller cancellation also cancel the wait on the gate.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _sweepCts.Token);

        await _gate.WaitAsync(priority, linked.Token).ConfigureAwait(false);

        // Pool might have been disposed after wait returned:
        if (Volatile.Read(ref _disposed) == 1)
        {
            ThrowDisposed();
        }

        // Try to reuse an existing idle connection.
        while (TryTakeIdle(out var item))
        {
            if (item.IsExpired(IdleTimeout))
            {
                DiscardConnection(item.Connection);
                continue;
            }

            if (_validator != null && item.IsExpired(_validateAfterIdle))
            {
                bool healthy;
                try
                {
                    healthy = await _validator(item.Connection, linked.Token).ConfigureAwait(false);
                }
                catch
                {
                    DiscardConnection(item.Connection);
                    ReleaseGateIfOpen();
                    throw;
                }

                if (!healthy)
                {
                    DiscardConnection(item.Connection);
                    continue;
                }
            }

            TriggerConnectionPoolChangedEvent();
            return BuildLock(item.Connection);
        }

        // Need a fresh connection.
        T conn;
        try
        {
            conn = await _factory(linked.Token).ConfigureAwait(false);
        }
        catch
        {
            ReleaseGateIfOpen(); // free the permit on failure
            throw;
        }

        if (Volatile.Read(ref _disposed) == 1)
        {
            DisposeConnection(conn);
            ThrowDisposed();
        }

        Interlocked.Increment(ref _live);
        TriggerConnectionPoolChangedEvent();
        return BuildLock(conn);

        ConnectionLock<T> BuildLock(T c)
            => new(c, Return, Destroy);

        static void ThrowDisposed()
            => throw new ObjectDisposedException(nameof(ConnectionPool<T>));
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
            _maxConnections
        ));
    }

    private bool TryTakeIdle(out Pooled item)
    {
        lock (_idleLock)
            return _idleConnections.TryPop(out item);
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
            if (Volatile.Read(ref _disposed) == 0)
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

    /* =================== idle sweeper (background) ================================= */

    private async Task SweepLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(IdleTimeout / 2);
            while (await timer.WaitForNextTickAsync(_sweepCts.Token).ConfigureAwait(false))
                SweepOnce();
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
            while (_idleConnections.TryPop(out var item))
            {
                if (item.IsExpired(IdleTimeout, now))
                    expired.Add(item.Connection);
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

    private static void DisposeConnection(T conn)
    {
        if (conn is IDisposable d)
            d.Dispose();
    }

    /* -------------------------- IAsyncDisposable ---------------------------------- */

    public async ValueTask DisposeAsync()
    {
        lock (_idleLock)
        {
            if (_disposed == 1) return;
            _disposed = 1;
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
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    /* ----------------------------- IDisposable ------------------------------------ */

    public void Dispose()
    {
        _ = DisposeAsync().AsTask(); // fire-and-forget synchronous path
    }
}
