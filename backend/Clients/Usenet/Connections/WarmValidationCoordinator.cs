namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Applies the live warm-validation setting across every provider pool in one
/// client graph. The limit is read for each admission so a settings update can
/// lower the cap without rebuilding the pools; existing work is allowed to
/// finish and no replacement work is admitted until it falls below the new cap.
/// </summary>
public sealed class WarmValidationCoordinator(Func<int> concurrencyLimit)
{
    private static readonly TimeSpan AdmissionPollInterval = TimeSpan.FromMilliseconds(25);
    private readonly object _lock = new();
    private int _active;

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                var limit = Math.Max(1, concurrencyLimit());
                if (_active < limit)
                {
                    _active++;
                    return new Lease(this);
                }
            }

            await Task.Delay(AdmissionPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            if (_active <= 0)
                throw new InvalidOperationException("Warm-validation lease released too many times.");
            _active--;
        }
    }

    private sealed class Lease(WarmValidationCoordinator owner) : IDisposable
    {
        private WarmValidationCoordinator? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
