using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class PlaybackReadAheadTrackerTests
{
    [Fact]
    public void Complete_TimeWeightsAverageAndIgnoresTerminalDrainForMinimum()
    {
        var clock = new ManualTimeProvider();
        var tracker = new PlaybackReadAheadTracker(clock);

        tracker.ProducerStarted(targetBytes: 10);
        clock.Advance(TimeSpan.FromSeconds(1)); // Startup: nothing buffered yet.
        tracker.SegmentBuffered(10);
        clock.Advance(TimeSpan.FromSeconds(2));
        tracker.SegmentDequeued(4);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.ProducerCompleted(targetBytes: 10);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.SegmentDequeued(6); // Natural drain after the producer reached EOF.

        var result = tracker.Complete();

        Assert.Equal(5_000, result.MeasuredMilliseconds);
        Assert.Equal(32_000, result.ByteMilliseconds);
        Assert.Equal(6, result.MinimumBytes);
    }

    [Fact]
    public void Complete_HasNoMinimumWhenTheBufferNeverFills()
    {
        var clock = new ManualTimeProvider();
        var tracker = new PlaybackReadAheadTracker(clock);

        tracker.ProducerStarted(targetBytes: 10);
        tracker.SegmentBuffered(6);
        clock.Advance(TimeSpan.FromSeconds(1));
        tracker.ProducerCompleted(targetBytes: 10);
        tracker.SegmentDequeued(6);

        var result = tracker.Complete();

        Assert.Equal(6_000, result.ByteMilliseconds);
        Assert.Null(result.MinimumBytes);
    }

    [Fact]
    public void Complete_IgnoresReadAheadDropThatRecoversWithinOneSecond()
    {
        var clock = new ManualTimeProvider();
        var tracker = new PlaybackReadAheadTracker(clock);

        tracker.ProducerStarted(targetBytes: 10);
        tracker.SegmentBuffered(10);
        tracker.SegmentDequeued(10);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        tracker.SegmentBuffered(10);
        tracker.ProducerCompleted(targetBytes: 10);

        var result = tracker.Complete();

        Assert.Equal(10, result.MinimumBytes);
    }

    [Fact]
    public void Complete_RecordsReadAheadDropThatLastsOneSecond()
    {
        var clock = new ManualTimeProvider();
        var tracker = new PlaybackReadAheadTracker(clock);

        tracker.ProducerStarted(targetBytes: 10);
        tracker.SegmentBuffered(10);
        tracker.SegmentDequeued(10);
        clock.Advance(TimeSpan.FromSeconds(1));

        var result = tracker.Complete();

        Assert.Equal(0, result.MinimumBytes);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
