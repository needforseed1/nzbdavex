using NzbWebDAV.Queue;

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
}
