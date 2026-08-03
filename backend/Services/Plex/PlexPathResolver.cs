using NzbWebDAV.Config;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Plex;

/// <summary>
/// Resolves only identities carried by NzbDAVex .ids links. It never walks a
/// library and deliberately returns null rather than guessing from a title or
/// filename.
/// </summary>
public sealed class PlexPathResolver(ConfigManager configManager)
{
    public Guid? ResolveDavItemId(string? plexMediaPath)
    {
        if (string.IsNullOrWhiteSpace(plexMediaPath)) return null;
        if (TryExtractDavItemId(plexMediaPath, out var direct)) return direct;

        var localPath = MapToLocalPath(
            plexMediaPath,
            configManager.GetPlexPathPrefix(),
            configManager.GetPlexLocalPathPrefix());
        if (localPath is null) return null;

        try
        {
            var info = SymlinkAndStrmUtil.GetSymlinkOrStrmInfo(new FileInfo(localPath));
            var target = info switch
            {
                SymlinkAndStrmUtil.SymlinkInfo symlink => symlink.TargetPath,
                SymlinkAndStrmUtil.StrmInfo strm => strm.TargetUrl,
                _ => null,
            };
            return TryExtractDavItemId(target, out var id) ? id : null;
        }
        catch (Exception e) when (e is IOException
                                  or UnauthorizedAccessException
                                  or ArgumentException
                                  or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? MapToLocalPath(
        string mediaPath,
        string? plexPathPrefix,
        string? localPathPrefix)
    {
        var hasPlexPrefix = !string.IsNullOrWhiteSpace(plexPathPrefix);
        var hasLocalPrefix = !string.IsNullOrWhiteSpace(localPathPrefix);
        if (!hasPlexPrefix && !hasLocalPrefix)
            return mediaPath;
        if (hasPlexPrefix != hasLocalPrefix) return null;

        var prefix = Path.TrimEndingDirectorySeparator(plexPathPrefix!.Trim());
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!mediaPath.StartsWith(prefix, comparison)) return null;
        if (mediaPath.Length > prefix.Length
            && mediaPath[prefix.Length] is not ('/' or '\\'))
            return null;

        var remainder = mediaPath[prefix.Length..].TrimStart('/', '\\');
        var localRoot = Path.GetFullPath(localPathPrefix!.Trim());
        var mapped = Path.GetFullPath(Path.Join(localRoot, remainder));
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(localRoot)
                                + Path.DirectorySeparatorChar;
        if (!mapped.Equals(localRoot, comparison)
            && !mapped.StartsWith(rootWithSeparator, comparison))
            return null;
        return mapped;
    }

    internal static bool TryExtractDavItemId(string? value, out Guid id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string path;
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
            path = Uri.UnescapeDataString(uri.AbsolutePath);
        else
            path = value.Trim();

        var segments = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        var marker = Array.FindIndex(
            segments,
            segment => segment.Equals(".ids", StringComparison.OrdinalIgnoreCase));
        if (marker < 0 || marker == segments.Length - 1) return false;

        var candidate = segments[^1];
        return Guid.TryParse(candidate, out id)
               || Guid.TryParse(Path.GetFileNameWithoutExtension(candidate), out id);
    }
}
