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

    [Theory]
    [InlineData("release.part001.rar", 1)]
    [InlineData("release.PART042.RAR", 42)]
    [InlineData("release.r00", 0)]
    [InlineData("release.R127", 127)]
    [InlineData("release.rar", -1)]
    [InlineData("release.001", null)]
    [InlineData("release.mkv", null)]
    public void RecognizesOnlySupportedRarVolumeNames(string filename, int? expected)
    {
        Assert.Equal(expected, RarProcessor.GetPartNumberFromFilename(filename));
    }
}
