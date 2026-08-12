namespace NzbWebDAV.Services;

/// <summary>
/// Measures queued playback data at article boundaries. The average is weighted
/// by elapsed time rather than producer-event count. The minimum is armed only
/// after the configured target has been reached and a lower level must remain
/// unrecovered for one second before it qualifies. It is no longer lowered
/// after every producer reaches EOF, avoiding brief article-boundary dips and
/// the meaningless startup and terminal zeroes that every healthy stream has.
/// </summary>
internal sealed class PlaybackReadAheadTracker(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan MinimumQualificationDuration = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _lastTimestamp;
    private long _bufferedBytes;
    private long _activeTargetBytes;
    private int _activeProducers;
    private double _byteMilliseconds;
    private double _measuredMilliseconds;
    private long? _minimumBytes;
    private long? _pendingMinimumBytes;
    private long _pendingMinimumStartedAt;
    private bool _minimumArmed;
    private bool _measuring;
    private bool _sawBufferedBytes;
    private bool _completed;

    public void ProducerStarted(long targetBytes)
    {
        lock (_lock)
        {
            if (_completed) return;
            var now = Advance();
            QualifyPendingMinimum(now);
            _activeProducers++;
            _activeTargetBytes += Math.Max(1, targetBytes);
            StartMeasuring(now);
        }
    }

    public void ProducerCompleted(long targetBytes)
    {
        lock (_lock)
        {
            if (_completed) return;
            var now = Advance();
            QualifyPendingMinimum(now);
            if (_activeProducers > 0) _activeProducers--;
            _activeTargetBytes = Math.Max(0, _activeTargetBytes - Math.Max(1, targetBytes));
            UpdatePendingMinimum(now);
        }
    }

    public void SegmentBuffered(long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            if (_completed) return;
            var now = Advance();
            QualifyPendingMinimum(now);
            _bufferedBytes += bytes;
            _sawBufferedBytes = true;
            StartMeasuring(now);

            if (!_minimumArmed
                && _activeTargetBytes > 0
                && _bufferedBytes >= _activeTargetBytes)
            {
                _minimumArmed = true;
                _minimumBytes = Min(_minimumBytes, _bufferedBytes);
                _pendingMinimumBytes = null;
            }
            else
                UpdatePendingMinimum(now);
        }
    }

    public void SegmentDequeued(long bytes)
    {
        if (bytes <= 0) return;
        lock (_lock)
        {
            if (_completed) return;
            var now = Advance();
            QualifyPendingMinimum(now);
            _bufferedBytes = Math.Max(0, _bufferedBytes - bytes);
            UpdatePendingMinimum(now);

            if (_bufferedBytes == 0 && _activeProducers == 0)
            {
                _minimumArmed = false;
                _pendingMinimumBytes = null;
                _measuring = false;
            }
        }
    }

    public PlaybackReadAheadSnapshot Complete()
    {
        lock (_lock)
        {
            if (!_completed)
            {
                var now = Advance();
                QualifyPendingMinimum(now);
                _completed = true;
                _measuring = false;
            }

            return !_sawBufferedBytes
                ? new PlaybackReadAheadSnapshot(0, 0, null)
                : new PlaybackReadAheadSnapshot(
                    _byteMilliseconds,
                    _measuredMilliseconds,
                    _minimumBytes);
        }
    }

    private void StartMeasuring(long now)
    {
        if (_measuring) return;
        _lastTimestamp = now;
        _measuring = true;
    }

    private long Advance()
    {
        var now = _timeProvider.GetTimestamp();
        if (!_measuring) return now;
        var elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds;
        _lastTimestamp = now;
        if (elapsed <= 0) return now;
        _byteMilliseconds += _bufferedBytes * elapsed;
        _measuredMilliseconds += elapsed;
        return now;
    }

    private void QualifyPendingMinimum(long now)
    {
        if (_pendingMinimumBytes is not { } candidate) return;
        if (_timeProvider.GetElapsedTime(_pendingMinimumStartedAt, now)
            < MinimumQualificationDuration)
            return;

        _minimumBytes = Min(_minimumBytes, candidate);
        _pendingMinimumBytes = null;

        // If the buffer fell farther while the first low-water level was being
        // qualified, let that deeper level earn its own continuous second.
        if (_activeProducers > 0 && _bufferedBytes < candidate)
        {
            _pendingMinimumBytes = _bufferedBytes;
            _pendingMinimumStartedAt = now;
        }
    }

    private void UpdatePendingMinimum(long now)
    {
        // Once all producers have reached EOF, the remaining decline is the
        // inevitable terminal drain, not evidence that read-ahead failed.
        if (!_minimumArmed || _activeProducers <= 0 || _minimumBytes is not { } minimum)
        {
            _pendingMinimumBytes = null;
            return;
        }

        if (_bufferedBytes >= minimum)
        {
            _pendingMinimumBytes = null;
            return;
        }

        if (_pendingMinimumBytes is null || _bufferedBytes > _pendingMinimumBytes.Value)
        {
            _pendingMinimumBytes = _bufferedBytes;
            _pendingMinimumStartedAt = now;
        }
    }

    private static long Min(long? current, long candidate) =>
        current is null ? candidate : Math.Min(current.Value, candidate);
}

internal readonly record struct PlaybackReadAheadSnapshot(
    double ByteMilliseconds,
    double MeasuredMilliseconds,
    long? MinimumBytes);
