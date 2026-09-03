namespace NzbWebDAV.Utils;

public static class DebounceUtil
{
    public static CancellableDebounce CreateCancellableDebounce(TimeSpan timespan) => new(timespan);

    public sealed class CancellableDebounce : IDisposable
    {
        private readonly object _synchronizationLock = new();
        private readonly TimeSpan _timespan;
        private readonly Timer _flushTimer;
        private DateTime _lastInvocationTime;
        private Action? _pendingAction;
        private int _activeCallbacks;
        private bool _disposed;

        internal CancellableDebounce(TimeSpan timespan)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(timespan, TimeSpan.Zero);
            _timespan = timespan;
            _flushTimer = new Timer(_ => InvokePending());
        }

        public void Invoke(Action actionToInvoke)
        {
            Action? invokeNow = null;
            lock (_synchronizationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = DateTime.UtcNow;
                var elapsed = now - _lastInvocationTime;
                if (elapsed >= _timespan && _pendingAction is null)
                {
                    _lastInvocationTime = now;
                    invokeNow = actionToInvoke;
                }
                else
                {
                    _pendingAction = actionToInvoke;
                    var delay = _timespan - elapsed;
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    _flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
                }
            }

            invokeNow?.Invoke();
        }

        public void CancelPending()
        {
            lock (_synchronizationLock)
            {
                if (_disposed) return;
                _pendingAction = null;
                _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                while (_activeCallbacks > 0)
                    Monitor.Wait(_synchronizationLock);
            }
        }

        private void InvokePending()
        {
            Action? action;
            lock (_synchronizationLock)
            {
                if (_disposed || _pendingAction is null) return;
                action = _pendingAction;
                _pendingAction = null;
                _lastInvocationTime = DateTime.UtcNow;
                _activeCallbacks++;
            }

            try
            {
                action.Invoke();
            }
            finally
            {
                lock (_synchronizationLock)
                {
                    _activeCallbacks--;
                    Monitor.PulseAll(_synchronizationLock);
                }
            }
        }

        public void Dispose()
        {
            CancelPending();
            lock (_synchronizationLock)
            {
                if (_disposed) return;
                _disposed = true;
                _flushTimer.Dispose();
            }
        }
    }

    public static Action<Action> CreateDebounce(TimeSpan timespan)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timespan, TimeSpan.Zero);
        var synchronizationLock = new object();
        DateTime lastInvocationTime = default;
        var isFlushScheduled = false;
        Action? pendingAction = null;
        Timer? flushTimer = null;

        return actionToInvoke =>
        {
            Action? invokeNow = null;
            lock (synchronizationLock)
            {
                var now = DateTime.Now;
                var elapsed = now - lastInvocationTime;
                if (elapsed >= timespan && !isFlushScheduled)
                {
                    lastInvocationTime = now;
                    invokeNow = actionToInvoke;
                }
                else
                {
                    pendingAction = actionToInvoke;
                    if (!isFlushScheduled)
                    {
                        isFlushScheduled = true;
                        var delay = timespan - elapsed;
                        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                        flushTimer ??= new Timer(_ =>
                        {
                            Action? trailingAction;
                            lock (synchronizationLock)
                            {
                                isFlushScheduled = false;
                                lastInvocationTime = DateTime.Now;
                                trailingAction = pendingAction;
                                pendingAction = null;
                            }

                            trailingAction?.Invoke();
                        });
                        flushTimer.Change(delay, Timeout.InfiniteTimeSpan);
                    }
                }
            }

            invokeNow?.Invoke();
        };
    }

    public static Action<Action> RunOnlyOnce()
    {
        var isAlreadyRan = false;
        return actionToMaybeInvoke =>
        {
            if (isAlreadyRan) return;
            isAlreadyRan = true;
            actionToMaybeInvoke?.Invoke();
        };
    }
}
