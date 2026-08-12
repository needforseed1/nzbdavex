using MemoryPack;
using ZstdSharp;

namespace NzbWebDAV.Database;

public class BlobStore
{
    private static readonly int CompressionLevel = 1;
    private static readonly string ConfigPath = DavDatabaseContext.ConfigPath;
    private static readonly Lock LockObj = new();

    private static string GetBlobPath(Guid id)
    {
        var guidStr = id.ToString("N"); // Without hyphens
        var firstTwo = guidStr[..2];
        var nextTwo = guidStr.Substring(2, 2);
        var fileName = id.ToString(); // With hyphens for readability

        return Path.Combine(ConfigPath, "blobs", firstTwo, nextTwo, fileName);
    }

    private static FileStream OpenTemporaryBlobWrite(string blobPath, out string temporaryPath)
    {
        var directory = Path.GetDirectoryName(blobPath);
        temporaryPath = $"{blobPath}.{Guid.NewGuid():N}.tmp";

        // Acquire the temporary handle inside the lock so cleanup cannot
        // remove the directory between CreateDirectory and opening the file.
        FileStream fileStream;
        lock (LockObj)
        {
            Directory.CreateDirectory(directory!);
            fileStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        return fileStream;
    }

    public static async Task WriteBlob(Guid id, Stream stream)
    {
        await WriteBlobFile(GetBlobPath(id), stream);
    }

    internal static async Task WriteBlobFile(string blobPath, Stream stream)
    {
        await WriteBlobFile(blobPath, fileStream => stream.CopyToAsync(fileStream));
    }

    public static async Task WriteBlob<T>(Guid id, T blob)
    {
        await WriteBlobFile(GetBlobPath(id), async fileStream =>
        {
            await using var compressionStream = new CompressionStream(
                fileStream,
                CompressionLevel,
                leaveOpen: true);
            await MemoryPackSerializer.SerializeAsync(compressionStream, blob);
        });
    }

    private static async Task WriteBlobFile(string blobPath, Func<Stream, Task> writeAsync)
    {
        var fileStream = OpenTemporaryBlobWrite(blobPath, out var temporaryPath);
        try
        {
            await using (fileStream.ConfigureAwait(false))
            {
                await writeAsync(fileStream).ConfigureAwait(false);
                await fileStream.FlushAsync().ConfigureAwait(false);
            }

            // The temporary file lives beside the destination, so this rename
            // is atomic. Readers retain the previous complete blob until the
            // replacement is fully written and closed, then new readers see
            // the complete replacement without an exclusive-writer window.
            lock (LockObj)
            {
                File.Move(temporaryPath, blobPath, overwrite: true);
            }
        }
        finally
        {
            // Serialization/copy failures must leave the prior blob intact and
            // must not accumulate abandoned temporary files.
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static Stream? ReadBlob(Guid id)
    {
        var blobPath = GetBlobPath(id);
        return File.Exists(blobPath) ? File.OpenRead(blobPath) : null;
    }

    public static async Task<T?> ReadBlob<T>(Guid id)
    {
        var stream = ReadBlob(id);
        if (stream == null) return default;
        await using var fileStream = stream;
        await using var decompressionStream = new DecompressionStream(fileStream);
        return await MemoryPackSerializer.DeserializeAsync<T>(decompressionStream);
    }

    public static void Delete(Guid id)
    {
        var blobPath = GetBlobPath(id);

        // Delete the file
        if (File.Exists(blobPath))
        {
            File.Delete(blobPath);
        }

        lock (LockObj)
        {
            // Clean up empty directories
            // Structure: CONFIG_PATH/blobs/{firstTwo}/{nextTwo}/{fileName}
            var nextTwoDir = Path.GetDirectoryName(blobPath);
            var firstTwoDir = Path.GetDirectoryName(nextTwoDir);

            TryDeleteEmptyDirectory(nextTwoDir);
            TryDeleteEmptyDirectory(firstTwoDir);
        }
    }

    private static void TryDeleteEmptyDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        if (!Directory.Exists(directory)) return;
        if (!IsDirectoryEmpty(directory)) return;
        Directory.Delete(directory, recursive: false);
    }

    private static bool IsDirectoryEmpty(string path)
    {
        return !Directory.EnumerateFileSystemEntries(path).Any();
    }
}
