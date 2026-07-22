using System.Collections.Concurrent;

namespace NzbWebDAV.Clients.Usenet.Contexts;

/// <summary>
/// Coordinates fallback ARTICLE/BODY work for one preparation run. Primary
/// requests remain unrestricted. Each fallback hostname starts with one
/// probation request and, after a clean response, may serve a small bounded
/// burst without consuming every warm connection configured for that host.
/// </summary>
internal sealed class PrepFallbackContext : IDisposable
{
    internal const int MaxConcurrentRequestsPerHost = 12;
    private static readonly TimeSpan CapacityRefreshInterval = TimeSpan.FromMilliseconds(25);

    private readonly ConcurrentDictionary<string, HostAdmissionGate> _hosts =
        new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public async ValueTask<PrepFallbackLease?> EnterAsync(
        MultiConnectionNntpClient provider,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return await GetHost(provider)
            .EnterAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void MarkResponsive(MultiConnectionNntpClient provider) =>
        GetHost(provider).MarkResponsive();

    /// <summary>
    /// Marks every configured account using this hostname unavailable for the
    /// rest of the prep run. Returns true only to the first failing request;
    /// concurrent siblings are peer-aborted rather than counted as additional
    /// provider timeouts.
    /// </summary>
    public bool MarkUnavailable(MultiConnectionNntpClient provider) =>
        GetHost(provider).MarkUnavailable();

    private HostAdmissionGate GetHost(MultiConnectionNntpClient provider)
    {
        var host = _hosts.GetOrAdd(provider.Host, static _ => new HostAdmissionGate());
        host.ObserveCapacity(provider.MaxConnections);
        return host;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var host in _hosts.Values) host.Dispose();
        _hosts.Clear();
    }

    internal sealed class PrepFallbackLease(
        HostAdmissionGate owner,
        CancellationToken hostFailureToken) : IDisposable
    {
        private HostAdmissionGate? _owner = owner;

        public CancellationToken HostFailureToken { get; } = hostFailureToken;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }

    internal sealed class HostAdmissionGate : IDisposable
    {
        private readonly SemaphoreSlim _slots = new(1, MaxConcurrentRequestsPerHost);
        private readonly CancellationTokenSource _failureCts = new();
        private int _admissionLimit = 1;
        private int _maximumObservedCapacity = 1;
        private int _responsive;
        private int _unavailable;
        private int _disposed;

        public async ValueTask<PrepFallbackLease?> EnterAsync(
            CancellationToken cancellationToken)
        {
            while (true)
            {
                if (Volatile.Read(ref _unavailable) != 0) return null;
                if (await _slots.WaitAsync(CapacityRefreshInterval, cancellationToken)
                        .ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _unavailable) != 0)
                    {
                        _slots.Release();
                        return null;
                    }

                    return new PrepFallbackLease(this, _failureCts.Token);
                }
            }
        }

        public void MarkResponsive()
        {
            if (Volatile.Read(ref _unavailable) != 0 ||
                Interlocked.Exchange(ref _responsive, 1) != 0)
                return;

            RaiseAdmissionLimit();
        }

        public void ObserveCapacity(int maximumConnections)
        {
            var observed = Math.Clamp(
                maximumConnections,
                1,
                MaxConcurrentRequestsPerHost);
            while (true)
            {
                var current = Volatile.Read(ref _maximumObservedCapacity);
                if (observed <= current) break;
                if (Interlocked.CompareExchange(
                        ref _maximumObservedCapacity,
                        observed,
                        current) == current)
                    break;
            }

            if (Volatile.Read(ref _responsive) != 0) RaiseAdmissionLimit();
        }

        private void RaiseAdmissionLimit()
        {
            var desired = Volatile.Read(ref _maximumObservedCapacity);
            while (true)
            {
                var current = Volatile.Read(ref _admissionLimit);
                if (desired <= current) return;
                if (Interlocked.CompareExchange(
                        ref _admissionLimit,
                        desired,
                        current) != current)
                    continue;
                _slots.Release(desired - current);
                return;
            }
        }

        public bool MarkUnavailable()
        {
            if (Interlocked.CompareExchange(ref _unavailable, 1, 0) != 0)
                return false;

            try
            {
                _failureCts.Cancel();
            }
            catch (AggregateException)
            {
                // Cancellation is advisory. Individual attempts still retain
                // their own bounded command deadline if a callback misbehaves.
            }

            return true;
        }

        public void Exit() => _slots.Release();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _failureCts.Dispose();
            _slots.Dispose();
        }
    }
}
