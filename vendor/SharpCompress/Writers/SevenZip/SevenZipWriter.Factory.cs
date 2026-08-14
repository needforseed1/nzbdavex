#if NET8_0_OR_GREATER
using System.IO;

namespace SharpCompress.Writers.SevenZip;

public partial class SevenZipWriter : IWriterOpenable<SevenZipWriterOptions>
{
    /// <summary>
    /// Opens a new SevenZipWriter for the specified file path.
    /// </summary>
    public static IWriter OpenWriter(string filePath, SevenZipWriterOptions writerOptions)
    {
        filePath.NotNullOrEmpty(nameof(filePath));
        return OpenWriter(new FileInfo(filePath), writerOptions);
    }

    /// <summary>
    /// Opens a new SevenZipWriter for the specified file.
    /// </summary>
    public static IWriter OpenWriter(FileInfo fileInfo, SevenZipWriterOptions writerOptions)
    {
        fileInfo.NotNull(nameof(fileInfo));
        return new SevenZipWriter(fileInfo.OpenWrite(), writerOptions);
    }

    /// <summary>
    /// Opens a new SevenZipWriter for the specified stream.
    /// </summary>
    public static IWriter OpenWriter(Stream stream, SevenZipWriterOptions writerOptions)
    {
        stream.NotNull(nameof(stream));
        return new SevenZipWriter(stream, writerOptions);
    }

    /// <summary>
    /// Opens a new async SevenZipWriter for the specified file path.
    /// </summary>
    public static IAsyncWriter OpenAsyncWriter(string filePath, SevenZipWriterOptions writerOptions)
    {
        return (IAsyncWriter)OpenWriter(filePath, writerOptions);
    }

    /// <summary>
    /// Opens a new async SevenZipWriter for the specified stream.
    /// </summary>
    public static IAsyncWriter OpenAsyncWriter(Stream stream, SevenZipWriterOptions writerOptions)
    {
        return (IAsyncWriter)OpenWriter(stream, writerOptions);
    }

    /// <summary>
    /// Opens a new async SevenZipWriter for the specified file.
    /// </summary>
    public static IAsyncWriter OpenAsyncWriter(
        FileInfo fileInfo,
        SevenZipWriterOptions writerOptions
    )
    {
        return (IAsyncWriter)OpenWriter(fileInfo, writerOptions);
    }
}
#endif
