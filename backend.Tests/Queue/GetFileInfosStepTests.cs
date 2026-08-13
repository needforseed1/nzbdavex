using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public class GetFileInfosStepTests
{
    [Fact]
    public async Task HashMatchedPar2UsesYencSizeWhenIndexerSegmentBytesAreWrong()
    {
        const long actualFileSize = 1_000_000;
        var firstBytes = new byte[16 * 1024];
        "Rar!\u001a\u0007\u0000"u8.CopyTo(firstBytes);
        var nzbFile = new NzbFile { Subject = "\"obfuscated.part001.rar\"" };
        nzbFile.Segments.Add(new NzbSegment
        {
            MessageId = "segment-1",
            // Reproduce an indexer whose NZB byte metadata is roughly double
            // the actual yEnc/PAR2 file size.
            Bytes = actualFileSize * 2,
        });
        var fetched = new FetchFirstSegmentsStep.NzbFileWithFirstSegment
        {
            NzbFile = nzbFile,
            First16KB = firstBytes,
            Header = YencHeader("obfuscated.part001.rar", actualFileSize),
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UtcNow,
        };
        var descriptor = await FileDescriptorAsync(
            firstBytes, "restored.part001.rar", actualFileSize);

        var info = Assert.Single(GetFileInfosStep.GetFileInfos([fetched], [descriptor]));

        Assert.Equal(actualFileSize, info.FileSize);
        Assert.Equal("restored.part001.rar", info.FileName);
    }

    [Fact]
    public async Task YencSizeMismatchRejectsHashMatchedPar2Descriptor()
    {
        const long yencFileSize = 1_000_000;
        const long conflictingPar2Size = 950_000;
        var firstBytes = new byte[16 * 1024];
        "Rar!\u001a\u0007\u0000"u8.CopyTo(firstBytes);
        var nzbFile = new NzbFile { Subject = "\"subject.part001.rar\"" };
        nzbFile.Segments.Add(new NzbSegment
        {
            MessageId = "segment-1",
            Bytes = yencFileSize,
        });
        var fetched = new FetchFirstSegmentsStep.NzbFileWithFirstSegment
        {
            NzbFile = nzbFile,
            First16KB = firstBytes,
            Header = YencHeader("header.part001.rar", yencFileSize),
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UtcNow,
        };
        var descriptor = await FileDescriptorAsync(
            firstBytes, "incorrect.part001.rar", conflictingPar2Size);

        var info = Assert.Single(GetFileInfosStep.GetFileInfos([fetched], [descriptor]));

        Assert.Null(info.FileSize);
        Assert.Equal("subject.part001.rar", info.FileName);
    }

    [Fact]
    public void RarMagicPrefersUsableHeaderVolumeNameOverMkvSubject()
    {
        var selected = GetFileInfosStep.SelectFilename(
            par2FileName: null,
            subjectFileName: "The.Sopranos.S01E11.mkv",
            headerFileName: "obfuscated.part001.rar",
            isRar: true);

        Assert.Equal("obfuscated.part001.rar", selected);
    }

    [Fact]
    public void RarMagicKeepsPar2PriorityAmongUsableRarNames()
    {
        var selected = GetFileInfosStep.SelectFilename(
            par2FileName: "restored.part001.rar",
            subjectFileName: "subject.part001.rar",
            headerFileName: "header.part001.rar",
            isRar: true);

        Assert.Equal("restored.part001.rar", selected);
    }

    [Fact]
    public void RarMagicRetainsExistingFallbackWhenNoRarNameExists()
    {
        var selected = GetFileInfosStep.SelectFilename(
            par2FileName: null,
            subjectFileName: "The.Sopranos.S01E11.mkv",
            headerFileName: "obfuscated.001",
            isRar: true);

        Assert.Equal("The.Sopranos.S01E11.mkv", selected);
    }

    [Fact]
    public void NonRarPayloadRetainsNormalFilenamePriority()
    {
        var selected = GetFileInfosStep.SelectFilename(
            par2FileName: null,
            subjectFileName: "video.mkv",
            headerFileName: "misleading.part001.rar",
            isRar: false);

        Assert.Equal("video.mkv", selected);
    }

    private static UsenetYencHeader YencHeader(string fileName, long fileSize) => new()
    {
        FileName = fileName,
        FileSize = fileSize,
        LineLength = 128,
        PartNumber = 1,
        TotalParts = 2,
        PartSize = fileSize / 2,
        PartOffset = 0,
    };

    private static async Task<FileDesc> FileDescriptorAsync(
        byte[] firstBytes,
        string fileName,
        long fileSize)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var paddedNameLength = (nameBytes.Length + 3) / 4 * 4;
        var body = new byte[56 + paddedNameLength];
        MD5.HashData(firstBytes).CopyTo(body, 32);
        BitConverter.GetBytes((ulong)fileSize).CopyTo(body, 48);
        nameBytes.CopyTo(body, 56);

        var descriptor = new FileDesc(new Par2PacketHeader
        {
            PacketLength = (ulong)(Marshal.SizeOf<Par2PacketHeader>() + body.Length),
        });
        await descriptor.ReadAsync(new MemoryStream(body));
        return descriptor;
    }
}
