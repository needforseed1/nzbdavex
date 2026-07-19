namespace NzbWebDAV.Queue;

/// <summary>
/// Coordinates the queue-manager's idle sleep with wake-up requests. A wake-up
/// may be immediate or scheduled for a future time (for example a queue item's
/// `PauseUntil`). Scheduling never delays an earlier pending wake-up.
/// </summary>
internal sealed class SleepingQueueSignal : IDisposable
{
    private readonly Lock _lock = new();
    private CancellationTokenSource _cts = new();
    private DateTime? _pendingWakeAt;
    private bool _disposed;

    /// <summary>
    /// Wakes the queue immediately, or at <paramref name="dateTime"/> when it is
    /// in the future. Requests later than an already-pending wake-up are ignored;
    /// the earlier wake-up re-evaluates the queue and reschedules as needed.
    /// </summary>
    public void Awaken(DateTime? dateTime = null)
    {
        var cancelAfter = dateTime.HasValue ? (dateTime.Value - DateTime.Now) : (TimeSpan?)null;
        lock (_lock)
        {
            if (_disposed) return;
            if (cancelAfter.HasValue && cancelAfter.Value > TimeSpan.Zero)
            {
                if (_pendingWakeAt.HasValue && _pendingWakeAt.Value <= dateTime!.Value) return;
                _pendingWakeAt = dateTime;
                _cts.CancelAfter(cancelAfter.Value);
            }
            else
            {
                _pendingWakeAt = DateTime.Now;
                _cts.Cancel();
            }
        }
    }

    /// <summary>
    /// Sleeps until <paramref name="maxSleep"/> elapses, a wake-up fires, or the
    /// caller's token is cancelled. Always returns normally; the caller's loop
    /// condition observes its own token. A wake-up resets the signal for the
    /// next sleep.
    /// </summary>
    public async Task WaitAsync(TimeSpan maxSleep, CancellationToken cancellationToken)
    {
        CancellationToken sleepToken;
        lock (_lock)
        {
            if (_disposed) return;
            sleepToken = _cts.Token;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                sleepToken, cancellationToken);
            await Task.Delay(maxSleep, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (sleepToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                Reset();
        }
    }

    private void Reset()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pendingWakeAt = null;
            if (!_cts.TryReset())
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Dispose();
        }
    }
}
