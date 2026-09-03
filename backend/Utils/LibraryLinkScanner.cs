using NzbWebDAV.Config;

namespace NzbWebDAV.Utils;

public static class LibraryLinkScanner
{
    public sealed record DavItemLink(
        string LinkPath,
        Guid DavItemId,
        SymlinkAndStrmUtil.ISymlinkOrStrmInfo SymlinkOrStrmInfo);

    public sealed record ScanResult(
        IReadOnlyList<DavItemLink> Links,
        IReadOnlyList<Guid> UniqueDavItemIds,
        string? ErrorMessage)
    {
        public bool IsComplete => ErrorMessage is null;
        public int RawLinkCount => Links.Count;
    }

    public static async Task<ScanResult> ScanAsync(
        ConfigManager configManager,
        Action<int>? onLinkedFileFound = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var libraryRoot = configManager.GetLibraryDir()
                              ?? throw new InvalidOperationException("Library directory is not configured.");
            var mountRoot = configManager.GetRcloneMountDir();
            var links = new List<DavItemLink>();
            var rawScan = await SymlinkAndStrmUtil
                .ScanAllSymlinksAndStrmsAsync(libraryRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!rawScan.IsComplete)
                return new ScanResult([], [], rawScan.ErrorMessage);

            foreach (var item in rawScan.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DavItemLink? link;
                try
                {
                    link = GetDavItemLink(item, mountRoot);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    var linkPath = item switch
                    {
                        SymlinkAndStrmUtil.SymlinkInfo symlink => symlink.SymlinkPath,
                        SymlinkAndStrmUtil.StrmInfo strm => strm.StrmPath,
                        _ => libraryRoot
                    };
                    return new ScanResult([], [], $"Failed to parse library link `{linkPath}`: {e.Message}");
                }

                if (link is null) continue;
                links.Add(link);
                onLinkedFileFound?.Invoke(links.Count);
            }

            var uniqueIds = links
                .Select(x => x.DavItemId)
                .Distinct()
                .ToList();
            return new ScanResult(links, uniqueIds, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return new ScanResult([], [], e.Message);
        }
    }

    internal static DavItemLink? GetDavItemLink(
        SymlinkAndStrmUtil.ISymlinkOrStrmInfo item,
        string mountRoot)
    {
        return item switch
        {
            SymlinkAndStrmUtil.SymlinkInfo symlink => GetDavItemLink(symlink, mountRoot),
            SymlinkAndStrmUtil.StrmInfo strm => GetDavItemLink(strm),
            _ => throw new InvalidOperationException("Unknown library link type.")
        };
    }

    private static DavItemLink? GetDavItemLink(
        SymlinkAndStrmUtil.SymlinkInfo symlink,
        string mountRoot)
    {
        var linkDirectory = Path.GetDirectoryName(symlink.SymlinkPath)
                            ?? throw new InvalidOperationException($"Could not resolve parent directory for `{symlink.SymlinkPath}`.");
        var targetPath = Path.IsPathRooted(symlink.TargetPath)
            ? Path.GetFullPath(symlink.TargetPath)
            : Path.GetFullPath(symlink.TargetPath, linkDirectory);
        var normalizedMountRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountRoot));
        var relativeTarget = Path.GetRelativePath(normalizedMountRoot, targetPath);
        if (EscapesRoot(relativeTarget)) return null;

        var parts = relativeTarget.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length < 2 || !string.Equals(parts[0], ".ids", PathComparison)) return null;
        var id = ParseDavItemId(parts[^1], symlink.SymlinkPath);
        return new DavItemLink(symlink.SymlinkPath, id, symlink);
    }

    private static DavItemLink? GetDavItemLink(SymlinkAndStrmUtil.StrmInfo strm)
    {
        var targetUrl = new Uri(strm.TargetUrl);
        var parts = targetUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3
            || !string.Equals(parts[0], "view", StringComparison.Ordinal)
            || !string.Equals(parts[1], ".ids", StringComparison.Ordinal)) return null;
        var id = ParseDavItemId(parts[^1], strm.StrmPath);
        return new DavItemLink(strm.StrmPath, id, strm);
    }

    private static Guid ParseDavItemId(string pathSegment, string linkPath)
    {
        var idText = Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(pathSegment));
        return Guid.TryParse(idText, out var id)
            ? id
            : throw new InvalidDataException($"Library link `{linkPath}` has an invalid dav-item id.");
    }

    private static bool EscapesRoot(string relativePath)
    {
        return relativePath == ".."
               || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
               || Path.IsPathRooted(relativePath);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
