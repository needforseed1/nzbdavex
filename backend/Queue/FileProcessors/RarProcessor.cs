using System.Text.RegularExpressions;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using SharpCompress.Common.Rar.Headers;

namespace NzbWebDAV.Queue.FileProcessors;

public class RarProcessor(
    GetFileInfosStep.FileInfo fileInfo,
    INntpClient usenetClient,
    string? password,
    CancellationToken ct
) : BaseProcessor
{
    public override async Task<BaseProcessor.Result?> ProcessAsync()
    {
        var (headers, partSize) = await GetRequiredHeadersAsync().ConfigureAwait(false);
        var archiveName = GetArchiveName();
        var partNumber = GetPartNumber(headers);
        return new Result()
        {
            StoredFileSegments = headers
                .Where(x => x.HeaderType == HeaderType.File)
                .Select(x => new StoredFileSegment()
                {
                    NzbFile = fileInfo.NzbFile,
                    PartSize = partSize,
                    ArchiveName = archiveName,
                    PartNumber = partNumber,
                    PathWithinArchive = x.GetFileName(),
                    ByteRangeWithinPart = LongRange.FromStartAndSize(
                        x.GetDataStartPosition(),
                        x.GetAdditionalDataSize()
                    ),
                    AesParams = x.GetAesParams(password),
                    FileUncompressedSize = x.GetUncompressedSize(),
                    ReleaseDate = fileInfo.ReleaseDate,
                }).ToArray(),
        };
    }

    private async Task<(List<IRarHeader> Headers, long PartSize)> GetRequiredHeadersAsync()
    {
        if (fileInfo.First16KB is { Length: > 0 } firstBytes)
        {
            await using var prefix = new MemoryStream(firstBytes, writable: false);
            var firstHeaders = await RarUtil.ReadHeadersUntilFirstFileAsync(prefix, password, ct)
                .ConfigureAwait(false);
            var firstFileHeader = firstHeaders.FirstOrDefault(x => x.HeaderType == HeaderType.File);

            // A split-after file entry continues in the next RAR volume. By
            // RAR's volume layout it consumes the remainder of this volume, so
            // there cannot be another file entry here. Crucially, parse this
            // from the 16KB prefix retained by the initial prep stage before
            // opening NzbFileStream: the queue cache otherwise downloads the
            // entire first article before exposing a single header byte.
            if (CanStopAfterFirstFileHeader(firstFileHeader?.GetIsSplitAfter() == true))
                return (firstHeaders, await GetFileSizeAsync().ConfigureAwait(false));
        }

        // A non-split entry can end before another file begins in this same
        // volume. Preserve the full scan for those boundary/final volumes so
        // multi-file archives retain every stored-file segment.
        await using var stream = await GetNzbFileStream().ConfigureAwait(false);
        var headers = await RarUtil.GetRarHeadersAsync(stream, password, ct).ConfigureAwait(false);
        return (headers, stream.Length);
    }

    internal static bool CanStopAfterFirstFileHeader(bool isSplitAfter) => isSplitAfter;

    private string GetArchiveName()
    {
        // remove the .rar extension and remove the .partXX if it exists
        var sansExtension = Path.GetFileNameWithoutExtension(fileInfo.FileName);
        sansExtension = Regex.Replace(sansExtension, @"\.part\d+$", "");
        return sansExtension;
    }

    private PartNumber GetPartNumber(List<IRarHeader> rarHeaders)
    {
        var partNumber = new PartNumber()
        {
            PartNumberFromHeader = GetPartNumberFromHeaders(rarHeaders),
            PartNumberFromFilename = GetPartNumberFromFilename(fileInfo.FileName),
        };

        if (partNumber.PartNumberFromHeader == null && partNumber.PartNumberFromFilename == null)
            throw new Exception("Could not determine part number for RAR file.");

        return partNumber;
    }

    private static int? GetPartNumberFromHeaders(List<IRarHeader> headers)
    {
        headers = headers.Where(x => x.HeaderType is HeaderType.Archive or HeaderType.EndArchive).ToList();

        var archiveHeader = headers.FirstOrDefault(x => x.HeaderType is HeaderType.Archive);
        var archiveVolumeNumber = archiveHeader?.GetVolumeNumber();
        if (archiveVolumeNumber != null) return archiveVolumeNumber!.Value;

        var endHeader = headers.FirstOrDefault(x => x.HeaderType == HeaderType.EndArchive);
        var endVolumeNumber = endHeader?.GetVolumeNumber();
        if (endVolumeNumber != null) return endVolumeNumber!.Value;

        if (archiveHeader?.GetIsFirstVolume() == true) return -1;
        return null;
    }

    private static int? GetPartNumberFromFilename(string filename)
    {
        // handle the `.partXXX.rar` format
        var partMatch = Regex.Match(filename, @"\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        if (partMatch.Success)
            return int.Parse(partMatch.Groups[1].Value);

        // handle the `.rXXX` format
        var rMatch = Regex.Match(filename, @"\.r(\d+)$", RegexOptions.IgnoreCase);
        if (rMatch.Success)
            return int.Parse(rMatch.Groups[1].Value);

        // handle the `.rar` format.
        if (filename.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
            return -1;

        //  could not determine from filename
        return null;
    }

    private async Task<NzbFileStream> GetNzbFileStream()
    {
        var filesize = await GetFileSizeAsync().ConfigureAwait(false);
        return usenetClient.GetFileStream(fileInfo.NzbFile, filesize, articleBufferSize: 0);
    }

    private async Task<long> GetFileSizeAsync() => fileInfo.FileSize
        ?? await usenetClient.GetFileSizeAsync(fileInfo.NzbFile, ct).ConfigureAwait(false);

    public new class Result : BaseProcessor.Result
    {
        public required StoredFileSegment[] StoredFileSegments { get; init; }
    }

    public class StoredFileSegment
    {
        public required NzbFile NzbFile { get; init; }
        public required long PartSize { get; init; }
        public required string ArchiveName { get; init; }
        public required PartNumber PartNumber { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public required string PathWithinArchive { get; init; }
        public required LongRange ByteRangeWithinPart { get; init; }
        public required AesParams? AesParams { get; init; }

        public required long FileUncompressedSize { get; init; }
    }

    public record PartNumber
    {
        public int? PartNumberFromHeader { get; init; }
        public int? PartNumberFromFilename { get; init; }
    }
}
