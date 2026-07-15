using NzbWebDAV.Queue;
using NzbWebDAV.Models.Nzb;

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

    private static NzbFile NzbFile(string subject, int start, int count)
    {
        var file = new NzbFile { Subject = subject };
        file.Segments.AddRange(Enumerable.Range(start, count).Select(index =>
            new NzbSegment { Bytes = 1, MessageId = $"segment-{index}" }));
        return file;
    }
}
