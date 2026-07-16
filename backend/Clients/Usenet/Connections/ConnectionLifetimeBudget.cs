namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Bounds NNTP sockets and connection handshakes across every provider pool and
/// across client-graph reloads. A successful reservation remains charged until
/// the owning pool disposes the corresponding connection.
/// </summary>
public sealed class ConnectionLifetimeBudget
{
    private readonly SemaphoreSlim _connectionSlots;
    private readonly SemaphoreSlim _creationSlots;
    private readonly int _maxConnections;
    private readonly int _maxConcurrentCreations;
    private int _foregroundWaiters;

    public ConnectionLifetimeBudget(int maxConnections, int maxConcurrentCreations)
    {
        if (maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (maxConcurrentCreations <= 0 || maxConcurrentCreations > maxConnections)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentCreations));

        _maxConnections = maxConnections;
        _maxConcurrentCreations = maxConcurrentCreations;
        _connectionSlots = new SemaphoreSlim(maxConnections, maxConnections);
        _creationSlots = new SemaphoreSlim(maxConcurrentCreations, maxConcurrentCreations);
    }

    internal int ReservedConnections => _maxConnections - _connectionSlots.CurrentCount;
    internal int ActiveCreations => _maxConcurrentCreations - _creationSlots.CurrentCount;
    internal int ForegroundWaiters => Volatile.Read(ref _foregroundWaiters);

    internal async ValueTask<CreationReservation> ReserveCreationAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _foregroundWaiters);
        try
        {
            // A task that is only waiting for handshake capacity must not consume
            // one of the longer-lived socket reservations. With the opposite
            // ordering, a saturated handshake gate could let queued attempts claim
            // every remaining socket slot without starting a single connection.
            await _creationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                return new CreationReservation(this);
            }
            catch
            {
                _creationSlots.Release();
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _foregroundWaiters);
        }
    }

    /// <summary>
    /// Reserves immediately available capacity for speculative background work.
    /// Prewarming must never queue ahead of a real acquisition or wait for a
    /// provider that is already using the application-wide socket budget.
    /// </summary>
    internal bool TryReserveCreation(out CreationReservation reservation)
    {
        reservation = null!;
        if (Volatile.Read(ref _foregroundWaiters) > 0 || !_connectionSlots.Wait(0))
            return false;

        if (Volatile.Read(ref _foregroundWaiters) > 0 || !_creationSlots.Wait(0))
        {
            _connectionSlots.Release();
            return false;
        }

        reservation = new CreationReservation(this);
        return true;
    }

    public void ReleaseConnection() => _connectionSlots.Release();

    internal sealed class CreationReservation : IDisposable
    {
        private ConnectionLifetimeBudget? _owner;

        public CreationReservation(ConnectionLifetimeBudget owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Transfers the lifetime slot to the newly created connection while
        /// releasing the shorter-lived handshake slot.
        /// </summary>
        public void Commit()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            current?._creationSlots.Release();
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            current._creationSlots.Release();
            current._connectionSlots.Release();
        }
    }
}
