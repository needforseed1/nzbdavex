using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NzbWebDAV.Utils;

public static class SymlinkAndStrmUtil
{
    private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public sealed record ScanResult(IReadOnlyList<ISymlinkOrStrmInfo> Items, string? ErrorMessage)
    {
        public bool IsComplete => ErrorMessage is null;
    }

    public static async Task<ScanResult> ScanAllSymlinksAndStrmsAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var items = IsLinux
                ? await ScanAllSymlinksAndStrmsLinuxAsync(directoryPath, cancellationToken).ConfigureAwait(false)
                : GetAllSymlinksAndStrmsWindows(directoryPath).ToList();
            return new ScanResult(items, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return new ScanResult([], e.Message);
        }
    }

    private static async Task<List<ISymlinkOrStrmInfo>> ScanAllSymlinksAndStrmsLinuxAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "find",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { directoryPath, "(", "-type", "l", "-o", "-name", "*.strm", ")", "-print0" })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start library traversal.");
        await using var output = new MemoryStream();
        var outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"Library traversal failed with exit code {process.ExitCode}: {error.Trim()}");

        var paths = Encoding.UTF8.GetString(output.ToArray())
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var items = new List<ISymlinkOrStrmInfo>(paths.Length);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            try
            {
                if (fullPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    items.Add(new StrmInfo
                    {
                        StrmPath = fullPath,
                        TargetUrl = (await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false)).Trim()
                    });
                    continue;
                }

                var target = new FileInfo(fullPath).LinkTarget
                             ?? throw new IOException("Symbolic link has no target.");
                items.Add(new SymlinkInfo { SymlinkPath = fullPath, TargetPath = target });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new IOException($"Failed to read library link `{fullPath}`: {e.Message}", e);
            }
        }

        return items;
    }

    private static IEnumerable<ISymlinkOrStrmInfo> GetAllSymlinksAndStrmsWindows(string directoryPath)
    {
        return Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.AllDirectories)
            .Select(x => new FileInfo(x))
            .Select(GetSymlinkOrStrmInfo)
            .Where(x => x != null)
            .Select(x => x!);
    }

    public static ISymlinkOrStrmInfo? GetSymlinkOrStrmInfo(FileInfo x)
    {
        return IsStrm(x) ? new StrmInfo() { StrmPath = x.FullName, TargetUrl = File.ReadAllText(x.FullName).Trim() }
            : IsSymLink(x) ? new SymlinkInfo() { SymlinkPath = x.FullName, TargetPath = x.LinkTarget! }
            : null;
    }

    private static bool IsStrm(FileInfo x) =>
        x.Extension.Equals(".strm", StringComparison.CurrentCultureIgnoreCase);

    private static bool IsSymLink(FileInfo x) =>
        x.Attributes.HasFlag(FileAttributes.ReparsePoint) && x.LinkTarget is not null;

    public interface ISymlinkOrStrmInfo;

    public struct SymlinkInfo : ISymlinkOrStrmInfo
    {
        public required string SymlinkPath;
        public required string TargetPath;
    }

    public struct StrmInfo : ISymlinkOrStrmInfo
    {
        public required string StrmPath;
        public required string TargetUrl;
    }
}
