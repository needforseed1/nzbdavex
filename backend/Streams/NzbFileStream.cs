using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize
) : FastReadOnlyStream
{
    private long _position;
    private bool _disposed;
    private Stream? _innerStream;

    // Average yEnc-decoded size per segment in this file. Used to (a) zero-fill
    // missing segments mid-stream so the demuxer can resync instead of the
    // player closing on a truncated body, and (b) synthesize a probe range
    // when SeekSegment can't fetch a missing segment's yEnc header. yEnc
    // segments within a single NzbFile are produced uniformly except for the
    // tail, so the average is within a few percent of any real segment.
    private long ExpectedSegmentSize =>
        fileSegmentIds.Length > 0 ? Math.Max(1, fileSize / fileSegmentIds.Length) : 0;

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= fileSize) return 0;
        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        return _position;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        var avg = ExpectedSegmentSize;
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                try
                {
                    var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                    return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
                }
                catch (UsenetArticleNotFoundException e)
                {
                    // The probe segment itself is missing — fall back to a
                    // synthetic uniform-size range so interpolation can still
                    // converge. The actual body read of this segment (if it
                    // turns out to be the seek target) gets zero-filled by
                    // MultiSegmentStream.
                    Log.Warning(
                        "Seek probe hit missing article {SegmentId} (segment index {Index}). Using estimated range.",
                        e.SegmentId, guess);
                    var start = guess * avg;
                    var end = Math.Min(fileSize, start + avg);
                    return new LongRange(start, end);
                }
                catch (Exception e) when (articleBufferSize > 0 && !ct.IsCancellationRequested)
                {
                    Log.Warning(e,
                        "Seek probe transient failure on segment index {Index}. Using estimated range.", guess);
                    var start = guess * avg;
                    var end = Math.Min(fileSize, start + avg);
                    return new LongRange(start, end);
                }
            },
            ct
        ).ConfigureAwait(false);
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0) return GetMultiSegmentStream(0, failFastOnFirstSegment: true, cancellationToken);
        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, failFastOnFirstSegment: false, cancellationToken);
        await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive, cancellationToken)
            .ConfigureAwait(false);
        return stream;
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, bool failFastOnFirstSegment,
        CancellationToken cancellationToken)
    {
        var segmentIds = fileSegmentIds.AsMemory()[firstSegmentIndex..];
        return MultiSegmentStream.Create(segmentIds, usenetClient, articleBufferSize, ExpectedSegmentSize,
            failFastOnFirstSegment, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}