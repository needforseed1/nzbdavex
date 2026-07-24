using System.Diagnostics;

namespace NzbWebDAV.Services;

internal static class PlaybackTransferPump
{
    private const int CopyBufferSize = 64 * 1024;

    public static async Task CopyAsync(
        Stream source,
        Stream destination,
        PlaybackRequestDiagnostics diagnostics,
        long startOffset,
        long? endOffset,
        bool seekSource,
        Action<long, long>? onBytesServed,
        Action<Exception, long>? onSourceError,
        CancellationToken cancellationToken)
    {
        if (seekSource && startOffset > 0)
        {
            if (!source.CanSeek)
                throw new IOException("Cannot use range, because the source stream isn't seekable");
            source.Seek(startOffset, SeekOrigin.Begin);
        }

        var bytesToRead = endOffset - startOffset + 1 ?? long.MaxValue;
        var buffer = new byte[CopyBufferSize];
        var position = startOffset;

        while (bytesToRead > 0)
        {
            var requestedBytes = (int)Math.Min(bytesToRead, buffer.Length);
            var readTimer = Stopwatch.StartNew();
            int bytesRead;
            try
            {
                bytesRead = await source
                    .ReadAsync(buffer.AsMemory(0, requestedBytes), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                onSourceError?.Invoke(exception, position);
                throw;
            }
            readTimer.Stop();
            if (bytesRead == 0) return;

            var writeTimer = Stopwatch.StartNew();
            await destination
                .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                .ConfigureAwait(false);
            writeTimer.Stop();

            position += bytesRead;
            diagnostics.RecordTransfer(
                bytesRead,
                position,
                readTimer.ElapsedMilliseconds,
                writeTimer.ElapsedMilliseconds);
            onBytesServed?.Invoke(bytesRead, position);
            bytesToRead -= bytesRead;
        }
    }
}
