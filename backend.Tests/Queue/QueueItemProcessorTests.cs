using NzbWebDAV.Queue;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Tests.Queue;

public class QueueItemProcessorTests
{
    [Theory]
    [InlineData(50, 55)]
    [InlineData(123, 128)]
    [InlineData(150, 128)]
    [InlineData(240, 128)]
    public void ProcessorConcurrencyCapsMemoryHeavyFallbacks(int queueConnections, int expected)
    {
        Assert.Equal(expected, QueueItemProcessor.GetProcessorConcurrency(queueConnections));
    }

    [Theory]
    [InlineData(0, 155, false)]
    [InlineData(155, 155, false)]
    [InlineData(156, 155, true)]
    [InlineData(703, 155, true)]
    public void DefersHealthOnlyWhenProcessorsExceedOneConcurrencyWave(
        int processorCount,
        int processorConcurrency,
        bool expected)
    {
        Assert.Equal(expected,
            QueueItemProcessor.ShouldDeferHealthCheck(processorCount, processorConcurrency));
    }

    [Theory]
    [InlineData("release.vol000-001.par2", false)]
    [InlineData("release.nfo", false)]
    [InlineData("release.sfv", false)]
    [InlineData("release.srt", true)]
    [InlineData("release.mkv", true)]
    [InlineData("release.sample.mkv", true)]
    public void SkipsOnlyBlocklistedNonMediaFilesDuringProcessing(string fileName, bool expected)
    {
        var blocklist = new HashSet<string>
        {
            "*.nfo",
            "*.par2",
            "*.sfv",
            "*sample.mkv",
        };

        Assert.Equal(expected,
            QueueItemProcessor.ShouldProcessNonArchiveFile(fileName, blocklist));
    }

    [Fact]
    public void ProcessesPar2WhenUserRemovesItFromBlocklist()
    {
        Assert.True(QueueItemProcessor.ShouldProcessNonArchiveFile(
            "release.vol000-001.par2",
            ["*.nfo", "*.sfv"]));
    }

    [Fact]
    public void HealthPrimerSamplesAcrossTheWholeNzbWithoutMaterializingEveryArticle()
    {
        var files = new[]
        {
            NzbFile("first", 0, 5),
            NzbFile("second", 5, 5),
        };

        var selected = QueueItemProcessor.SelectHealthPrimeSegmentIds(files, 4);

        Assert.Equal(["segment-0", "segment-3", "segment-6", "segment-9"], selected);
    }

    [Fact]
    public void HealthPrimerSamplesOnlyTheArticlePopulationTheFullCheckCovers()
    {
        // Par2/junk articles are excluded from the full health check, so the
        // coverage sample must not judge providers on them.
        var files = new[]
        {
            NzbFile("\"release.vol000-001.par2\"", 0, 5),
            NzbFile("\"release.mkv\"", 5, 5),
        };

        var selected = QueueItemProcessor.SelectHealthPrimeSegmentIds(files, 3);

        Assert.All(selected, segmentId => Assert.Contains(
            int.Parse(segmentId["segment-".Length..]), Enumerable.Range(5, 5)));
    }

    [Fact]
    public void HealthPrimerFallsBackToAllFilesWhenSubjectsAreObfuscated()
    {
        var files = new[]
        {
            NzbFile("adf8a7e2b9c1", 0, 5),
            NzbFile("bd91c3a0e7f2", 5, 5),
        };

        var selected = QueueItemProcessor.SelectHealthPrimeSegmentIds(files, 4);

        Assert.Equal(["segment-0", "segment-3", "segment-6", "segment-9"], selected);
    }

    [Fact]
    public async Task HealthStartsImmediatelyWhenWarmupAlreadyCompleted()
    {
        using var warmupCancellation = new CancellationTokenSource();
        var healthStarted = false;

        await QueueItemProcessor.RunHealthCheckAfterWarmupAsync(
            Task.CompletedTask,
            warmupCancellation,
            CancellationToken.None,
            () =>
            {
                healthStarted = true;
                return Task.CompletedTask;
            });

        Assert.True(healthStarted);
        Assert.False(warmupCancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task StalledWarmupIsCancelledAtForegroundHandoff()
    {
        using var warmupCancellation = new CancellationTokenSource();
        var warmup = Task.Delay(Timeout.InfiniteTimeSpan, warmupCancellation.Token);
        var healthStarted = false;
        var graceExpired = false;

        await QueueItemProcessor.RunHealthCheckAfterWarmupAsync(
            warmup,
            warmupCancellation,
            CancellationToken.None,
            () =>
            {
                healthStarted = true;
                return Task.CompletedTask;
            },
            () => graceExpired = true,
            TimeSpan.Zero);

        Assert.True(graceExpired);
        Assert.True(warmupCancellation.IsCancellationRequested);
        Assert.True(healthStarted);
    }

    [Fact]
    public async Task WarmupThatIgnoresCancellationCannotBlockForegroundHealth()
    {
        using var warmupCancellation = new CancellationTokenSource();
        var warmupCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var healthStarted = false;

        await QueueItemProcessor.RunHealthCheckAfterWarmupAsync(
            warmupCompletion.Task,
            warmupCancellation,
            CancellationToken.None,
            () =>
            {
                healthStarted = true;
                return Task.CompletedTask;
            },
            handoffGrace: TimeSpan.Zero);

        Assert.True(warmupCancellation.IsCancellationRequested);
        Assert.True(healthStarted);
        Assert.False(warmupCompletion.Task.IsCompleted);

        warmupCompletion.SetResult();
    }

    [Fact]
    public void ProviderTimeoutWrappingCancellationIsNotCallerCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var providerTimeout = new CouldNotConnectToUsenetException(
            "Provider timed out.",
            new TimeoutException(
                "Attempt timed out.",
                new OperationCanceledException()));

        Assert.False(QueueItemProcessor.IsCallerCancellation(
            providerTimeout, callerCancellation.Token));
        Assert.True(providerTimeout.IsRetryableDownloadException());
    }

    [Fact]
    public void RequestedQueueCancellationIsCallerCancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        Assert.True(QueueItemProcessor.IsCallerCancellation(
            new OperationCanceledException(callerCancellation.Token),
            callerCancellation.Token));
    }

    [Fact]
    public void PipelinedProviderTimeoutIsRetryable()
    {
        Assert.True(new TimeoutException(
            "Pipelined STAT deadline expired.").IsRetryableDownloadException());
    }

    [Fact]
    public async Task FailedAttemptCancelsAndObservesItsWarmup()
    {
        using var warmupCancellation = new CancellationTokenSource();
        var cleanupObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var warmup = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, warmupCancellation.Token);
            }
            finally
            {
                cleanupObserved.TrySetResult();
            }
        });

        await QueueItemProcessor.CancelAndObserveWarmupAsync(
            warmup, warmupCancellation, "test");

        Assert.True(warmupCancellation.IsCancellationRequested);
        Assert.True(cleanupObserved.Task.IsCompletedSuccessfully);
        Assert.True(warmup.IsCanceled);
    }

    [Fact]
    public async Task WarmupThatIgnoresCancellationCannotBlockAttemptCleanup()
    {
        using var warmupCancellation = new CancellationTokenSource();
        var warmupCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await QueueItemProcessor.CancelAndObserveWarmupAsync(
            warmupCompletion.Task,
            warmupCancellation,
            "test",
            TimeSpan.Zero);

        Assert.True(warmupCancellation.IsCancellationRequested);
        Assert.False(warmupCompletion.Task.IsCompleted);
        warmupCompletion.SetResult();
    }

    private static NzbFile NzbFile(string subject, int start, int count)
    {
        var file = new NzbFile { Subject = subject };
        file.Segments.AddRange(Enumerable.Range(start, count).Select(index =>
            new NzbSegment { Bytes = 1, MessageId = $"segment-{index}" }));
        return file;
    }
}
