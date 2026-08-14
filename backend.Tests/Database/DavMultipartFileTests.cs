using MemoryPack;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Database;

public partial class DavMultipartFileTests
{
    [Fact]
    public void PendingHeaderPrefixSurvivesBlobRoundTrip()
    {
        var source = new DavMultipartFile
        {
            Id = Guid.NewGuid(),
            Metadata = new DavMultipartFile.Meta
            {
                IsLazy = true,
                PendingParts =
                [
                    new DavMultipartFile.PendingPart
                    {
                        SegmentIds = ["segment"],
                        SegmentIdByteRange = LongRange.FromStartAndSize(0, 100),
                        EstimatedDataSize = 90,
                        HeaderPrefix = [1, 2, 3, 4],
                    },
                ],
            },
        };

        var bytes = MemoryPackSerializer.Serialize(source);
        var roundTripped = MemoryPackSerializer.Deserialize<DavMultipartFile>(bytes);

        Assert.NotNull(roundTripped);
        Assert.Equal([1, 2, 3, 4], roundTripped.Metadata.PendingParts[0].HeaderPrefix);
    }

    [Fact]
    public void PendingHeaderPrefixDefaultsToNullForOlderBlobs()
    {
        var legacy = new LegacyPendingPart
        {
            SegmentIds = ["segment"],
            SegmentIdByteRange = LongRange.FromStartAndSize(0, 100),
            EstimatedDataSize = 90,
        };

        var bytes = MemoryPackSerializer.Serialize(legacy);
        var current = MemoryPackSerializer.Deserialize<DavMultipartFile.PendingPart>(bytes);

        Assert.NotNull(current);
        Assert.Equal(["segment"], current.SegmentIds);
        Assert.Equal(90, current.EstimatedDataSize);
        Assert.Null(current.HeaderPrefix);
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class LegacyPendingPart
    {
        [MemoryPackOrder(0)]
        public string[] SegmentIds { get; set; } = [];

        [MemoryPackOrder(1)]
        public LongRange? SegmentIdByteRange { get; set; }

        [MemoryPackOrder(2)]
        public long EstimatedDataSize { get; set; }
    }
}
