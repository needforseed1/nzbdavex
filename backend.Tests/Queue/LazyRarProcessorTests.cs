using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Queue.FileProcessors;
using NzbWebDAV.Streams;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

public class LazyRarProcessorTests
{
    [Fact]
    public async Task SkipsSmallCompanionBeforeSpanningPayload()
    {
        var firstVolume = BuildRar4FirstVolume(
            new RarEntry("release.nfo", UnpackedSize: 4, PackedData: [1, 2, 3, 4], SplitAfter: false),
            new RarEntry("movie.mkv", UnpackedSize: 1_000, PackedData: new byte[20], SplitAfter: true));
        using var client = new FixtureNntpClient(firstVolume);
        var firstPart = FileInfo("release.part001.rar", "part-1", firstVolume.LongLength);
        var secondPart = FileInfo("release.part002.rar", "part-2", fileSize: 1_000);

        var result = await new LazyRarProcessor(
                [firstPart, secondPart],
                client,
                new ConfigManager(),
                password: null,
                CancellationToken.None)
            .ProcessAsync();

        var lazy = Assert.IsType<LazyRarProcessor.Result>(result);
        Assert.Equal("movie.mkv", lazy.PathInArchive);
        Assert.Equal(1_000, lazy.TotalFileSize);
        Assert.Single(lazy.PendingParts);
    }

    [Fact]
    public async Task KeepsEagerFallbackWhenNoEntrySpansTheArchive()
    {
        var firstVolume = BuildRar4FirstVolume(
            new RarEntry("release.nfo", UnpackedSize: 4, PackedData: [1, 2, 3, 4], SplitAfter: false),
            new RarEntry("sample.mkv", UnpackedSize: 100, PackedData: new byte[20], SplitAfter: false));
        using var client = new FixtureNntpClient(firstVolume);
        var firstPart = FileInfo("release.part001.rar", "part-1", firstVolume.LongLength);
        var secondPart = FileInfo("release.part002.rar", "part-2", fileSize: 1_000);

        var result = await new LazyRarProcessor(
                [firstPart, secondPart],
                client,
                new ConfigManager(),
                password: null,
                CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task KeepsEagerFallbackWhenLargeEntryDoesNotContinueIntoNextVolume()
    {
        var firstVolume = BuildRar4FirstVolume(
            new RarEntry("release.nfo", UnpackedSize: 4, PackedData: [1, 2, 3, 4], SplitAfter: false),
            new RarEntry("movie.mkv", UnpackedSize: 1_000, PackedData: new byte[20], SplitAfter: false));
        using var client = new FixtureNntpClient(firstVolume);
        var firstPart = FileInfo("release.part001.rar", "part-1", firstVolume.LongLength);
        var secondPart = FileInfo("release.part002.rar", "part-2", fileSize: 1_000);

        var result = await new LazyRarProcessor(
                [firstPart, secondPart],
                client,
                new ConfigManager(),
                password: null,
                CancellationToken.None)
            .ProcessAsync();

        Assert.Null(result);
    }

    private static GetFileInfosStep.FileInfo FileInfo(string name, string segmentId, long fileSize)
    {
        var nzbFile = new NzbFile { Subject = $"\"{name}\"" };
        nzbFile.Segments.Add(new NzbSegment { MessageId = segmentId, Bytes = fileSize });
        return new GetFileInfosStep.FileInfo
        {
            NzbFile = nzbFile,
            FileName = name,
            FileSize = fileSize,
            ReleaseDate = DateTimeOffset.UnixEpoch,
            IsRar = true,
        };
    }

    private static byte[] BuildRar4FirstVolume(params RarEntry[] entries)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]);
        WriteHeader(writer, header =>
        {
            header.Write((byte)0x73);
            header.Write((ushort)0x0101); // volume + first volume
            header.Write((short)13);
            header.Write((ushort)0);
            header.Write(0u);
        });

        foreach (var entry in entries)
        {
            var name = Encoding.ASCII.GetBytes(entry.Name);
            var flags = (ushort)(0x8000 | (entry.SplitAfter ? 0x0002 : 0));
            WriteHeader(writer, header =>
            {
                header.Write((byte)0x74);
                header.Write(flags);
                header.Write((short)(32 + name.Length));
                header.Write((uint)entry.PackedData.Length);
                header.Write((uint)entry.UnpackedSize);
                header.Write((byte)3); // Unix
                header.Write(0u); // file CRC is irrelevant for header parsing
                header.Write(0u); // DOS timestamp
                header.Write((byte)20);
                header.Write((byte)0x30); // stored (m0)
                header.Write((short)name.Length);
                header.Write(0u);
                header.Write(name);
            });
            writer.Write(entry.PackedData);
        }

        WriteHeader(writer, header =>
        {
            header.Write((byte)0x7B);
            header.Write((ushort)0x0001); // next volume
            header.Write((short)7);
        });

        return output.ToArray();
    }

    private static void WriteHeader(BinaryWriter output, Action<BinaryWriter> writeBody)
    {
        using var bodyStream = new MemoryStream();
        using (var body = new BinaryWriter(bodyStream, Encoding.UTF8, leaveOpen: true))
            writeBody(body);
        var bytes = bodyStream.ToArray();
        output.Write(RarHeaderCrc(bytes));
        output.Write(bytes);
    }

    private static ushort RarHeaderCrc(byte[] bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }
        return (ushort)~crc;
    }

    private sealed record RarEntry(string Name, long UnpackedSize, byte[] PackedData, bool SplitAfter);

    private sealed class FixtureNntpClient(byte[] firstVolume) : NntpClient
    {
        private readonly UsenetYencHeader _header = new()
        {
            FileName = "release.part001.rar",
            FileSize = firstVolume.LongLength,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = firstVolume.LongLength,
            PartOffset = 0,
        };

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "222 body follows",
                Stream = new CachedYencStream(_header, new MemoryStream(firstVolume, writable: false)),
            });
        }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) => DecodedBodyAsync(segmentId, cancellationToken);

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
