namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Bounds NNTP sockets and connection handshakes across every provider pool and
/// across client-graph reloads. A successful reservation remains charged until
/// the owning pool disposes the corresponding connection.
/// </summary>
public sealed class ConnectionLifetimeBudget
{
    private readonly SemaphoreSlim _connectionSlots;
    private readonly SemaphoreSlim _standardConnectionSlots;
    private readonly SemaphoreSlim _creationSlots;
    private readonly SemaphoreSlim _foregroundCreationSlots;
    private readonly SemaphoreSlim _speculativeCreationSlots;
    private readonly int _maxConnections;
    private readonly int _maxConcurrentCreations;
    private readonly int _maxConcurrentForegroundCreations;
    private readonly int _maxConcurrentSpeculativeCreations;
    private int _foregroundWaiters;

    public ConnectionLifetimeBudget(int maxConnections, int maxConcurrentCreations)
    {
        if (maxConnections <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConnections));
        if (maxConcurrentCreations <= 0 || maxConcurrentCreations > maxConnections)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentCreations));

        _maxConnections = maxConnections;
        _maxConcurrentCreations = maxConcurrentCreations;
        _maxConcurrentSpeculativeCreations = maxConcurrentCreations == 1
            ? 1
            : Math.Max(1, maxConcurrentCreations / 4);
        _maxConcurrentForegroundCreations = maxConcurrentCreations == 1
            ? 1
            : maxConcurrentCreations - _maxConcurrentSpeculativeCreations;
        _connectionSlots = new SemaphoreSlim(maxConnections, maxConnections);
        _standardConnectionSlots = new SemaphoreSlim(maxConnections, maxConnections);
        _creationSlots = new SemaphoreSlim(maxConcurrentCreations, maxConcurrentCreations);
        _foregroundCreationSlots = new SemaphoreSlim(
            _maxConcurrentForegroundCreations,
            _maxConcurrentForegroundCreations);
        _speculativeCreationSlots = new SemaphoreSlim(
            _maxConcurrentSpeculativeCreations,
            _maxConcurrentSpeculativeCreations);
    }

    internal int ReservedConnections => _maxConnections - _connectionSlots.CurrentCount;
    internal int ActiveCreations => _maxConcurrentCreations - _creationSlots.CurrentCount;
    internal int ActiveSpeculativeCreations =>
        _maxConcurrentSpeculativeCreations - _speculativeCreationSlots.CurrentCount;
    internal int ForegroundWaiters => Volatile.Read(ref _foregroundWaiters);
    internal int AvailableStandardCapacity => _standardConnectionSlots.CurrentCount;

    internal async ValueTask<CreationReservation> ReserveCreationAsync(CancellationToken cancellationToken)
        => await ReserveCreationAsync(useRecoveryCapacity: false, cancellationToken).ConfigureAwait(false);

    internal async ValueTask<CreationReservation> ReserveCreationAsync(
        bool useRecoveryCapacity,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _foregroundWaiters);
        try
        {
            // A task that is only waiting for handshake capacity must not consume
            // one of the longer-lived socket reservations. With the opposite
            // ordering, a saturated handshake gate could let queued attempts claim
            // every remaining socket slot without starting a single connection.
            await _foregroundCreationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _creationSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!useRecoveryCapacity)
                        await _standardConnectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await _connectionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                        return new CreationReservation(
                            this,
                            !useRecoveryCapacity,
                            speculative: false,
                            foregroundLimited: true);
                    }
                    catch
                    {
                        if (!useRecoveryCapacity) _standardConnectionSlots.Release();
                        throw;
                    }
                }
                catch
                {
                    _creationSlots.Release();
                    throw;
                }
            }
            catch
            {
                _foregroundCreationSlots.Release();
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
        => TryReserveCreation(
            useRecoveryCapacity: false,
            allowWhileForegroundWaiting: false,
            out reservation);

    internal bool TryReserveCreation(
        bool useRecoveryCapacity,
        bool allowWhileForegroundWaiting,
        out CreationReservation reservation)
    {
        reservation = null!;
        var foregroundWaiting = Volatile.Read(ref _foregroundWaiters) > 0;
        if (!allowWhileForegroundWaiting && foregroundWaiting)
            return false;
        if (allowWhileForegroundWaiting &&
            foregroundWaiting &&
            !useRecoveryCapacity &&
            _standardConnectionSlots.CurrentCount <= _maxConcurrentForegroundCreations)
            return false;
        if (!_speculativeCreationSlots.Wait(0)) return false;

        var standardReserved = false;
        if (!useRecoveryCapacity)
        {
            if (!_standardConnectionSlots.Wait(0))
            {
                _speculativeCreationSlots.Release();
                return false;
            }
            standardReserved = true;
        }

        foregroundWaiting = Volatile.Read(ref _foregroundWaiters) > 0;
        if ((!allowWhileForegroundWaiting && foregroundWaiting) ||
            (allowWhileForegroundWaiting &&
             foregroundWaiting &&
             !useRecoveryCapacity &&
             _standardConnectionSlots.CurrentCount < _maxConcurrentForegroundCreations) ||
            !_connectionSlots.Wait(0))
        {
            if (standardReserved) _standardConnectionSlots.Release();
            _speculativeCreationSlots.Release();
            return false;
        }

        if ((!allowWhileForegroundWaiting && Volatile.Read(ref _foregroundWaiters) > 0) ||
            !_creationSlots.Wait(0))
        {
            _connectionSlots.Release();
            if (standardReserved) _standardConnectionSlots.Release();
            _speculativeCreationSlots.Release();
            return false;
        }

        reservation = new CreationReservation(
            this,
            standardReserved,
            speculative: true,
            foregroundLimited: false);
        return true;
    }

    internal bool TryReserveStandardCapacity(int count, out IDisposable reservation)
    {
        reservation = EmptyReservation.Instance;
        if (count <= 0) return true;

        var acquired = 0;
        while (acquired < count && _standardConnectionSlots.Wait(0))
            acquired++;
        if (acquired != count)
        {
            if (acquired > 0) _standardConnectionSlots.Release(acquired);
            return false;
        }

        reservation = new StandardCapacityReservation(_standardConnectionSlots, count);
        return true;
    }

    public void ReleaseConnection(bool usedRecoveryCapacity = false)
    {
        _connectionSlots.Release();
        if (!usedRecoveryCapacity) _standardConnectionSlots.Release();
    }

    internal sealed class CreationReservation : IDisposable
    {
        private ConnectionLifetimeBudget? _owner;
        private readonly bool _standardReserved;
        private readonly bool _speculative;
        private readonly bool _foregroundLimited;

        public CreationReservation(
            ConnectionLifetimeBudget owner,
            bool standardReserved,
            bool speculative,
            bool foregroundLimited)
        {
            _owner = owner;
            _standardReserved = standardReserved;
            _speculative = speculative;
            _foregroundLimited = foregroundLimited;
        }

        /// <summary>
        /// Transfers the lifetime slot to the newly created connection while
        /// releasing the shorter-lived handshake slot.
        /// </summary>
        public void Commit()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            current._creationSlots.Release();
            if (_speculative) current._speculativeCreationSlots.Release();
            if (_foregroundLimited) current._foregroundCreationSlots.Release();
        }

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null) return;
            current._creationSlots.Release();
            if (_speculative) current._speculativeCreationSlots.Release();
            if (_foregroundLimited) current._foregroundCreationSlots.Release();
            current._connectionSlots.Release();
            if (_standardReserved) current._standardConnectionSlots.Release();
        }
    }

    private sealed class StandardCapacityReservation(
        SemaphoreSlim slots,
        int count) : IDisposable
    {
        private SemaphoreSlim? _slots = slots;

        public void Dispose()
        {
            Interlocked.Exchange(ref _slots, null)?.Release(count);
        }
    }

    private sealed class EmptyReservation : IDisposable
    {
        public static readonly EmptyReservation Instance = new();

        public void Dispose()
        {
        }
    }
}
