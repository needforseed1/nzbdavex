using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Common.SevenZip;

namespace SharpCompress.Writers.SevenZip;

public partial class SevenZipWriter
{
    /// <summary>
    /// Asynchronously writes a file entry to the 7z archive.
    /// </summary>
    public override async ValueTask WriteAsync(
        string filename,
        Stream source,
        DateTime? modificationTime,
        CancellationToken cancellationToken = default
    )
    {
        if (finalized)
        {
            throw new ObjectDisposedException(
                nameof(SevenZipWriter),
                "Cannot write to a finalized archive."
            );
        }

        cancellationToken.ThrowIfCancellationRequested();

        filename = NormalizeFilename(filename);
        var progressStream = WrapWithProgress(source, filename);

        var isEmpty = source.CanSeek && source.Length == 0;

        if (isEmpty)
        {
            entries.Add(
                new SevenZipWriteEntry
                {
                    Name = filename,
                    ModificationTime = modificationTime,
                    IsDirectory = false,
                    IsEmpty = true,
                }
            );
            return;
        }

        var output = OutputStream.NotNull();
        var outputPosBefore = output.Position;
        var compressor = new SevenZipStreamsCompressor(output);
        var packed = await compressor
            .CompressAsync(
                progressStream,
                sevenZipOptions.CompressionType,
                sevenZipOptions.LzmaProperties,
                cancellationToken
            )
            .ConfigureAwait(false);

        var actuallyEmpty = packed.Folder.GetUnpackSize() == 0;
        if (!actuallyEmpty)
        {
            packedStreams.Add(packed);
        }
        else
        {
            output.Position = outputPosBefore;
            output.SetLength(outputPosBefore);
        }

        entries.Add(
            new SevenZipWriteEntry
            {
                Name = filename,
                ModificationTime = modificationTime,
                IsDirectory = false,
                IsEmpty = isEmpty || actuallyEmpty,
            }
        );
    }

    /// <summary>
    /// Asynchronously writes a directory entry to the 7z archive.
    /// </summary>
    public override ValueTask WriteDirectoryAsync(
        string directoryName,
        DateTime? modificationTime,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteDirectory(directoryName, modificationTime);
        return new ValueTask();
    }
}
