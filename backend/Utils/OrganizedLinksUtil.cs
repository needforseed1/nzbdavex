using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Utils;

/// <summary>
/// Note: In this class, a `Link` refers to either a symlink or strm file.
/// </summary>
public static class OrganizedLinksUtil
{
    private static readonly Dictionary<Guid, string> Cache = new();

    public sealed record LinkLookupResult(string? LinkPath, string? ErrorMessage)
    {
        public bool IsComplete => ErrorMessage is null;
    }

    /// <summary>
    /// Searches organized media library for a symlink or strm pointing to the given target
    /// </summary>
    /// <param name="targetDavItem">The given target</param>
    /// <param name="configManager">The application config</param>
    /// <returns>The path to a symlink or strm in the organized media library that points to the given target.</returns>
    public static async Task<LinkLookupResult> GetLinkAsync(
        DavItem targetDavItem,
        ConfigManager configManager,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (TryGetLinkFromCache(targetDavItem, configManager, out var cachedPath))
                return new LinkLookupResult(cachedPath, null);
        }
        catch (Exception e)
        {
            return new LinkLookupResult(null, $"Failed to verify cached library link: {e.Message}");
        }

        var scan = await LibraryLinkScanner
            .ScanAsync(configManager, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!scan.IsComplete)
            return new LinkLookupResult(null, scan.ErrorMessage);

        string? result = null;
        lock (Cache)
        {
            foreach (var link in scan.Links)
            {
                Cache[link.DavItemId] = link.LinkPath;
                if (link.DavItemId == targetDavItem.Id)
                    result = link.LinkPath;
            }
        }

        return new LinkLookupResult(result, null);
    }

    private static bool TryGetLinkFromCache
    (
        DavItem targetDavItem,
        ConfigManager configManager,
        out string? linkFromCache
    )
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(targetDavItem.Id, out linkFromCache)) return false;
            try
            {
                if (Verify(linkFromCache, targetDavItem, configManager)) return true;
                Cache.Remove(targetDavItem.Id);
                linkFromCache = null;
                return false;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                Cache.Remove(targetDavItem.Id);
                linkFromCache = null;
                return false;
            }
        }
    }

    private static bool Verify(string linkFromCache, DavItem targetDavItem, ConfigManager configManager)
    {
        var fileInfo = new FileInfo(linkFromCache);
        var symlinkOrStrmInfo = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(fileInfo);
        if (symlinkOrStrmInfo == null) return false;
        var davItemLink = LibraryLinkScanner.GetDavItemLink(
            symlinkOrStrmInfo,
            configManager.GetRcloneMountDir());
        return davItemLink?.DavItemId == targetDavItem.Id;
    }
}
