using System.Security.Cryptography;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Queue.DeobfuscationSteps._3.GetFileInfos;

public static class GetFileInfosStep
{
    public static List<FileInfo> GetFileInfos
    (
        List<FetchFirstSegmentsStep.NzbFileWithFirstSegment> files,
        List<FileDesc> par2FileDescriptors
    )
    {
        using var md5 = MD5.Create();
        var hashToFileDescMap = GetHashToFileDescMap(par2FileDescriptors);
        var filesInfos = files
            .Select(x => GetFileInfo(x, hashToFileDescMap, md5))
            .ToList();

        return filesInfos;
    }

    private static Dictionary<string, LinkedList<FileDesc>> GetHashToFileDescMap(List<FileDesc> par2FileDescriptors)
    {
        var hashToFileDescMap = new Dictionary<string, LinkedList<FileDesc>>();
        foreach (var descriptor in par2FileDescriptors)
        {
            var hash = BitConverter.ToString(descriptor.File16kHash);
            if (!hashToFileDescMap.TryGetValue(hash, out var list))
            {
                list = new LinkedList<FileDesc>();
                hashToFileDescMap[hash] = list;
            }
            list.AddLast(descriptor);
        }

        return hashToFileDescMap;
    }

    private static FileInfo GetFileInfo(
        FetchFirstSegmentsStep.NzbFileWithFirstSegment file,
        Dictionary<string, LinkedList<FileDesc>> hashToFiledescMap,
        MD5 md5
    )
    {
        var fileDesc = GetMatchingFileDescriptor(file, hashToFiledescMap, md5);
        var subjectFileName = file.NzbFile.GetSubjectFileName();
        var headerFileName = file.Header?.FileName ?? "";
        var par2FileName = fileDesc?.FileName ?? "";
        var isRar = file.HasRar4Magic() || file.HasRar5Magic();
        var filename = SelectFilename(par2FileName, subjectFileName, headerFileName, isRar);

        return new FileInfo()
        {
            NzbFile = file.NzbFile,
            FileName = filename,
            First16KB = file.First16KB,
            ReleaseDate = file.ReleaseDate,
            FileSize = (long?)fileDesc?.FileLength,
            IsRar = isRar,
        };
    }

    internal static string SelectFilename(
        string? par2FileName,
        string? subjectFileName,
        string? headerFileName,
        bool isRar)
    {
        var candidates = new List<(string? FileName, int Priority)>
        {
            (FileName: par2FileName, Priority: GetFilenamePriority(par2FileName, 3)),
            (FileName: subjectFileName, Priority: GetFilenamePriority(subjectFileName, 2)),
            (FileName: headerFileName, Priority: GetFilenamePriority(headerFileName, 1)),
        }.Where(x => x.FileName is not null).ToList();

        // The first bytes are stronger evidence than an NZB subject. Some
        // indexers label every posted file with the final MKV name even though
        // the yEnc/PAR2 filename contains the real RAR volume name. Retaining
        // that misleading subject leaves the RAR processor with no part number.
        // When the payload is definitively RAR, prefer any candidate that can
        // identify a RAR volume before applying the normal priorities.
        if (isRar)
        {
            var rarCandidates = candidates
                .Where(x => FilenameUtil.IsRarFile(x.FileName))
                .ToList();
            if (rarCandidates.Count > 0) candidates = rarCandidates;
        }

        return candidates.MaxBy(x => x.Priority).FileName ?? "";
    }

    private static int GetFilenamePriority(string? filename, int startingPriority)
    {
        var priority = startingPriority;
        if (string.IsNullOrWhiteSpace(filename)) return priority - 5000;
        if (ObfuscationUtil.IsProbablyObfuscated(filename)) priority -= 1000;
        if (FilenameUtil.IsImportantFileType(filename)) priority += 50;
        if (Path.GetExtension(filename).TrimStart('.').Length is >= 2 and <= 4) priority += 10;
        return priority;
    }

    private static FileDesc? GetMatchingFileDescriptor
    (
        FetchFirstSegmentsStep.NzbFileWithFirstSegment file,
        Dictionary<string, LinkedList<FileDesc>> hashToFiledescMap,
        MD5 md5
    )
    {
        var hash = !file.MissingFirstSegment ? BitConverter.ToString(md5.ComputeHash(file.First16KB!)) : "";
        if (!hashToFiledescMap.TryGetValue(hash, out var fileDescs)) return null;
        var fileDesc = fileDescs.First!.Value;
        if (fileDescs.Count > 1) fileDescs.RemoveFirst();
        if (fileDesc.FileLength > long.MaxValue) return null;
        var fileSize = (long)fileDesc.FileLength;
        return IsMatchingFileSize(fileSize, file)
            ? fileDesc
            : null;
    }

    private static bool IsMatchingFileSize(
        long par2FileSize,
        FetchFirstSegmentsStep.NzbFileWithFirstSegment file)
    {
        // A PAR2 descriptor is already tied to this payload by the MD5 hash
        // of its first 16 KiB. When yEnc also declares an exact whole-file
        // size, require those two independent metadata sources to agree and
        // ignore potentially inaccurate NZB <segment bytes> values.
        if (file.Header?.FileSize is > 0)
            return par2FileSize == file.Header.FileSize;

        // Older or malformed yEnc posts may omit the whole-file size. Retain
        // the existing conservative NZB-size heuristic for that case.
        return IsCloseToYencodedSize(par2FileSize, file.NzbFile.GetTotalYencodedSize());
    }

    private static bool IsCloseToYencodedSize(long fileSize, long totalYencodedSize)
    {
        var range = new LongRange(95 * totalYencodedSize / 100, totalYencodedSize);
        return range.Contains(fileSize);
    }

    public record FileInfo
    {
        public required NzbFile NzbFile { get; init; }
        public required string FileName { get; init; }
        public byte[]? First16KB { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }
        public long? FileSize { get; init; }
        public bool IsRar { get; init; }
    }
}
