using UsenetSharp.Concurrency;

namespace NzbWebDAV.Clients.Usenet.Concurrency;

/// <summary>
/// This semaphore maintains two separate queues for waiters:
///   1. A high-priority queue
///   2. A low-priority queue
///
/// When there are both high- and low- priority waiters in their respective queues,
/// dice are rolled to determine which to release, using the given odds from the
/// constructor.
///
/// These configurable odds prevent the high-priority queue from fully starving the
/// low-priority queue.
/// </summary>
public class PrioritizedSemaphore : IDisposable
{
    private readonly LinkedList<TaskCompletionSource<bool>> _highPriorityWaiters = [];
    private readonly LinkedList<TaskCompletionSource<bool>> _lowPriorityWaiters = [];
    private SemaphorePriorityOdds _priorityOdds;
    private int _maxAllowed;
    private readonly int _highPriorityReserve;
    private int _enteredCount;
    private bool _disposed = false;
    private readonly Lock _lock = new();
    private int _accumulatedOdds;

    public PrioritizedSemaphore(
        int initialAllowed,
        int maxAllowed,
        SemaphorePriorityOdds? priorityOdds = null,
        int highPriorityReserve = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialAllowed);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAllowed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialAllowed, maxAllowed);
        ArgumentOutOfRangeException.ThrowIfNegative(highPriorityReserve);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(highPriorityReserve, maxAllowed);
        _priorityOdds = priorityOdds ?? new SemaphorePriorityOdds { HighPriorityOdds = 100 };
        _highPriorityReserve = highPriorityReserve;
        _enteredCount = maxAllowed - initialAllowed;
        _maxAllowed = maxAllowed;
    }

    public Task WaitAsync(SemaphorePriority priority, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            if (_enteredCount < GetPriorityLimit(priority))
            {
                _enteredCount++;
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = priority == SemaphorePriority.High ? _highPriorityWaiters : _lowPriorityWaiters;
            var node = queue.AddLast(tcs);

            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(() =>
                {
                    var removed = false;
                    lock (_lock)
                    {
                        try
                        {
                            queue.Remove(node);
                            removed = true;
                        }
                        catch (InvalidOperationException)
                        {
                            // intentionally left blank
                        }
                    }

                    if (removed)
                        tcs.TrySetCanceled(cancellationToken);
                });

                tcs.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            }

            return tcs.Task;
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? toRelease;
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AsyncSemaphore));

            if (_enteredCount > _maxAllowed)
            {
                // if more threads have entered than are allowed,
                // then don't release any waiter.
                //
                // This can happen when the _maxAllowed gets
                // lowered through the UpdateMaxAllowed method.
                toRelease = null;
            }
            else if (_highPriorityWaiters.Count == 0)
            {
                // if there are no high-priority waiters,
                // release a low-priority waiter only if doing so preserves
                // the configured high-priority reserve.
                toRelease = CanReplaceReleasedPermit(SemaphorePriority.Low)
                    ? Release(_lowPriorityWaiters)
                    : null;
            }
            else if (_lowPriorityWaiters.Count == 0)
            {
                // if there are no low-priority waiters,
                // then release a high-priority waiter.
                toRelease = Release(_highPriorityWaiters);
            }
            else if (!CanReplaceReleasedPermit(SemaphorePriority.Low))
            {
                // Low-priority work cannot refill this permit until enough
                // capacity has drained to restore the high-priority reserve.
                toRelease = Release(_highPriorityWaiters);
            }
            else
            {
                // if there are both high-priority waiters and low-priority waiters,
                // then roll the dice to determine which to release, based on the given odds.
                _accumulatedOdds += _priorityOdds.LowPriorityOdds;
                var (one, two) = (_highPriorityWaiters, _lowPriorityWaiters);
                if (_accumulatedOdds >= 100)
                {
                    (one, two) = (two, one);
                    _accumulatedOdds -= 100;
                }

                toRelease = Release(one) ?? Release(two);
            }

            if (toRelease == null)
            {
                // if no waiters were ultimately released,
                // then decrease the entered count.
                _enteredCount--;
                if (_enteredCount < 0)
                {
                    throw new InvalidOperationException("The semaphore cannot be further released.");
                }

                return;
            }
        }

        toRelease.TrySetResult(true);
    }

    private static TaskCompletionSource<bool>? Release(LinkedList<TaskCompletionSource<bool>> queue)
    {
        while (queue.Count > 0)
        {
            var node = queue.First!;
            queue.RemoveFirst();

            // Skip canceled tasks
            if (!node.Value.Task.IsCanceled)
            {
                return node.Value;
            }
        }

        return null;
    }

    public int GetAvailableCount(SemaphorePriority priority)
    {
        lock (_lock)
        {
            return Math.Max(0, GetPriorityLimit(priority) - _enteredCount);
        }
    }

    public int GetAllowedCount(SemaphorePriority priority)
    {
        lock (_lock)
        {
            return GetPriorityLimit(priority);
        }
    }

    private bool CanReplaceReleasedPermit(SemaphorePriority priority) =>
        _enteredCount <= GetPriorityLimit(priority);

    private int GetPriorityLimit(SemaphorePriority priority)
    {
        if (priority == SemaphorePriority.High) return _maxAllowed;

        // If provider capacity is reduced at runtime, retain one slot for
        // background work instead of allowing the configured reserve to
        // deadlock prep and health checks.
        var effectiveReserve = Math.Min(
            _highPriorityReserve,
            Math.Max(0, _maxAllowed - 1));
        return _maxAllowed - effectiveReserve;
    }

    public void UpdateMaxAllowed(int newMaxAllowed)
    {
        lock (_lock)
        {
            _maxAllowed = newMaxAllowed;
        }
    }

    public void UpdatePriorityOdds(SemaphorePriorityOdds newPriorityOdds)
    {
        lock (_lock)
        {
            _priorityOdds = newPriorityOdds;
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource<bool>> waitersToCancel;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            waitersToCancel = _highPriorityWaiters.Concat(_lowPriorityWaiters).ToList();
            _highPriorityWaiters.Clear();
            _lowPriorityWaiters.Clear();
        }

        foreach (var tcs in waitersToCancel)
            tcs.TrySetException(new ObjectDisposedException(nameof(AsyncSemaphore)));
    }
}
