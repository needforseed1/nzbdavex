using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class DavMultipartFileStreamTests
{
    [Fact]
    public void EncryptedLazyStreamCanOpenBeforeTrailingVolumesResolve()
    {
        const long decodedSize = 106;
        var aesParams = new AesParams
        {
            DecodedSize = decodedSize,
            Iv = new byte[16],
            Key = new byte[16],
        };
        var multipartFile = new DavMultipartFile
        {
            Metadata = new DavMultipartFile.Meta
            {
                AesParams = aesParams,
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        FilePartByteRange = LongRange.FromStartAndSize(0, 32),
                    },
                ],
                IsLazy = true,
                PendingParts =
                [
                    new DavMultipartFile.PendingPart
                    {
                        EstimatedDataSize = decodedSize - 32,
                    },
                ],
            },
        };

        using var packed = new DavMultipartFileStream(
            multipartFile,
            usenetClient: null!,
            readAheadBytes: 0,
            resolver: null);

        Assert.Equal(112, packed.Length);
        using var decoded = new AesDecoderStream(packed, aesParams);
        Assert.Equal(decodedSize, decoded.Length);
        Assert.True(multipartFile.Metadata.IsLazy);
        Assert.Single(multipartFile.Metadata.PendingParts);
    }

    [Fact]
    public async Task NearEndSeekReadsResolvedTailWithoutOpeningTheUnresolvedPrefix()
    {
        var openedParts = new List<string>();
        var multipartFile = new DavMultipartFile
        {
            Metadata = new DavMultipartFile.Meta
            {
                FileParts = [FilePart("part-0", 100)],
                IsLazy = true,
                PendingParts = Enumerable.Range(1, 6)
                    .Select(index => new DavMultipartFile.PendingPart
                    {
                        SegmentIds = [$"part-{index}"],
                        EstimatedDataSize = 100,
                    })
                    .ToArray(),
                TailFileParts = [FilePart("part-7", 100)],
            },
        };
        using var stream = new DavMultipartFileStream(
            multipartFile,
            (part, offset) =>
            {
                var segmentId = part.SegmentIds[0];
                openedParts.Add(segmentId);
                var value = byte.Parse(segmentId["part-".Length..]);
                return new MemoryStream(
                    Enumerable.Repeat(value, (int)(part.FilePartByteRange.Count - offset)).ToArray(),
                    writable: false);
            },
            resolver: null,
            expectedLength: 800);

        stream.Seek(750, SeekOrigin.Begin);
        var buffer = new byte[10];
        var read = await stream.ReadAsync(buffer);

        Assert.Equal(10, read);
        Assert.All(buffer, value => Assert.Equal((byte)7, value));
        Assert.Equal(["part-7"], openedParts);
    }

    private static DavMultipartFile.FilePart FilePart(string segmentId, long size) => new()
    {
        SegmentIds = [segmentId],
        SegmentIdByteRange = LongRange.FromStartAndSize(0, size),
        FilePartByteRange = LongRange.FromStartAndSize(0, size),
    };
}
