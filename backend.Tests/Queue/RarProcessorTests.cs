using NzbWebDAV.Queue.FileProcessors;

namespace NzbWebDAV.Tests.Queue;

public class RarProcessorTests
{
    [Fact]
    public void SplitAfterEntryDoesNotRequireTailScan()
    {
        Assert.True(RarProcessor.CanStopAfterFirstFileHeader(isSplitAfter: true));
    }

    [Fact]
    public void FinalOrBoundaryEntryRetainsFullScan()
    {
        Assert.False(RarProcessor.CanStopAfterFirstFileHeader(isSplitAfter: false));
    }
}
