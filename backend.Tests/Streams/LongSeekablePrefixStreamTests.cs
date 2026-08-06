using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class LongSeekablePrefixStreamTests
{
    [Fact]
    public async Task SupportsLogicalPositionsBeyondMemoryStreamLimit()
    {
        await using var stream = new LongSeekablePrefixStream([1, 2, 3]);

        stream.Position = (long)int.MaxValue + 1;

        Assert.Equal((long)int.MaxValue + 1, stream.Position);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task ReadsPrefixAndSeeksWithoutAllocatingSparseRange()
    {
        await using var stream = new LongSeekablePrefixStream([1, 2, 3]);
        var buffer = new byte[2];

        Assert.Equal(2, await stream.ReadAsync(buffer));
        Assert.Equal([1, 2], buffer);
        Assert.Equal(3, stream.Seek(1, SeekOrigin.Current));
        Assert.Equal(0, await stream.ReadAsync(buffer));
    }

    [Fact]
    public async Task RejectsNegativeLogicalPositions()
    {
        await using var stream = new LongSeekablePrefixStream([1]);

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Position = -1);
    }
}
