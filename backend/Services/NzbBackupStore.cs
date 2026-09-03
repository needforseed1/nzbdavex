namespace NzbWebDAV.Services;

public static class NzbBackupStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task SaveAsync(
        Stream source,
        string fileName,
        string category,
        string backupLocation,
        int retentionCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionCount, 1);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var backupRoot = Path.GetFullPath(backupLocation);
            var destinationDirectory = Path.GetFullPath(Path.Combine(backupRoot, category));
            if (!IsWithinRoot(backupRoot, destinationDirectory))
                throw new InvalidOperationException("Category escapes the configured NZB backup directory.");

            Directory.CreateDirectory(destinationDirectory);

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(extension)) extension = ".nzb";

            var destinationPath = await CopyToUniquePathAsync(
                    source,
                    destinationDirectory,
                    baseName,
                    extension,
                    cancellationToken)
                .ConfigureAwait(false);

            EnforceRetention(backupRoot, retentionCount, destinationPath);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> CopyToUniquePathAsync(
        Stream source,
        string destinationDirectory,
        string baseName,
        string extension,
        CancellationToken cancellationToken)
    {
        for (var counter = 1;; counter++)
        {
            var suffix = counter == 1 ? "" : $" ({counter})";
            var destinationPath = Path.Combine(destinationDirectory, $"{baseName}{suffix}{extension}");

            FileStream destination;
            try
            {
                destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                continue;
            }

            try
            {
                await using (destination.ConfigureAwait(false))
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                return destinationPath;
            }
            catch
            {
                destination.Dispose();
                File.Delete(destinationPath);
                throw;
            }
        }
    }

    private static void EnforceRetention(string backupRoot, int retentionCount, string newestBackup)
    {
        var backups = EnumerateBackupFiles(backupRoot)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => string.Equals(file.FullName, newestBackup, PathComparison))
            .ThenBy(file => file.FullName, StringComparer.Ordinal)
            .Skip(retentionCount)
            .ToList();

        foreach (var backup in backups)
        {
            try
            {
                backup.Delete();
            }
            catch (FileNotFoundException)
            {
                // A file removed externally after enumeration already satisfies retention.
            }
        }
    }

    private static IEnumerable<string> EnumerateBackupFiles(string backupRoot)
    {
        if (!Directory.Exists(backupRoot)) yield break;

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(backupRoot);

        while (pendingDirectories.TryPop(out var directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetExtension(file), ".nzb", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                    pendingDirectories.Push(child);
            }
        }
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return string.Equals(root, candidate, PathComparison)
            || candidate.StartsWith(rootPrefix, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
