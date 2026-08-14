using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;

namespace NzbWebDAV.Tests.Queue;

public class GetFileInfosStepTests
{
    [Theory]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, "rar3")]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, "rar5")]
    public void RarMagicReportsKeyDerivationFormat(byte[] prefix, string expected)
    {
        var file = new FetchFirstSegmentsStep.NzbFileWithFirstSegment
        {
            NzbFile = new NzbFile { Subject = "archive.part001.rar" },
            Header = null,
            First16KB = prefix,
            MissingFirstSegment = false,
            ReleaseDate = DateTimeOffset.UnixEpoch,
        };

        Assert.Equal(expected, file.GetRarFormat());
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
}
