using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public class QueueItemProcessorTests
{
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
}
