using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public class SleepingQueueSignalTests
{
    [Fact]
    public async Task ScheduledAwakenEndsSleepNearTheAdvertisedTimeNotTheFullPollInterval()
    {
        using var signal = new SleepingQueueSignal();
        signal.Awaken(DateTime.Now.AddMilliseconds(150));

        var timer = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.InRange(timer.Elapsed, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ImmediateAwakenWakesASleepingQueuePromptly()
    {
        using var signal = new SleepingQueueSignal();
        var sleeping = signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);

        signal.Awaken();
        await sleeping.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LaterScheduledWakeUpDoesNotDelayAnEarlierPendingOne()
    {
        using var signal = new SleepingQueueSignal();
        signal.Awaken(DateTime.Now.AddMilliseconds(150));
        signal.Awaken(DateTime.Now.AddMinutes(5));

        var timer = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WakeUpBeforeSleepIsNotLostAndTheSignalResetsForTheNextSleep()
    {
        using var signal = new SleepingQueueSignal();
        signal.Awaken();

        // The pre-fired wake-up ends this sleep immediately.
        await signal.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // The signal was reset, so the next sleep actually sleeps.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task CallerCancellationEndsTheSleepWithoutThrowing()
    {
        using var signal = new SleepingQueueSignal();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await signal.WaitAsync(TimeSpan.FromMinutes(1), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }
}
