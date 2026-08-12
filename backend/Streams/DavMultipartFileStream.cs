using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using Serilog;

namespace NzbWebDAV.Streams;

public class DavMultipartFileStream : Stream
{
    private readonly DavMultipartFile _mpf;
    private readonly INntpClient _usenetClient;
    private readonly long _readAheadBytes;
    private readonly LazyRarResolver? _resolver;
    private readonly long _length;
    private readonly Func<DavMultipartFile.FilePart, long, Stream>? _partOpener;

    private long _position;
    private CombinedStream? _innerStream;
    private bool _disposed;

    public DavMultipartFileStream(
        DavMultipartFile mpf,
        INntpClient usenetClient,
        long readAheadBytes,
        LazyRarResolver? resolver,
        long? expectedLength = null)
        : this(mpf, usenetClient, readAheadBytes, resolver, expectedLength, partOpener: null)
    {
    }

    internal DavMultipartFileStream(
        DavMultipartFile mpf,
        Func<DavMultipartFile.FilePart, long, Stream> partOpener,
        LazyRarResolver? resolver,
        long expectedLength)
        : this(
            mpf,
            usenetClient: null!,
            readAheadBytes: 0,
            resolver: resolver,
            expectedLength: expectedLength,
            partOpener: partOpener)
    {
    }

    private DavMultipartFileStream(
        DavMultipartFile mpf,
        INntpClient usenetClient,
        long readAheadBytes,
        LazyRarResolver? resolver,
        long? expectedLength,
        Func<DavMultipartFile.FilePart, long, Stream>? partOpener)
    {
        _mpf = mpf;
        _usenetClient = usenetClient;
        _readAheadBytes = readAheadBytes;
        _resolver = resolver;
        _length = expectedLength ?? ComputeLength(mpf.Metadata);
        _partOpener = partOpener;

        if (_resolver != null
            && _mpf.Metadata.IsLazy
            && (_mpf.Metadata.PendingParts?.Length ?? 0) > 0)
        {
            // Fill the prefix in the background one volume at a time. A single
            // low-priority walk leaves capacity for a live tail seek to resolve
            // its final volume directly instead of flooding the provider pools
            // with every header at once. Sequential playback still shares the
            // in-flight next volume, and every result is persisted.
            _ = PreWarmAsync();
        }
    }

    // Background resolution of every trailing volume. Self-observing: a missing
    // or unreachable trailing volume must neither surface as an unobserved task
    // fault nor break playback — byte 0 and every volume up to the failure still
    // stream fine. If the player actually reaches the bad volume, the on-demand
    // read path raises the error there, in context.
    private async Task PreWarmAsync()
    {
        try
        {
            while (_mpf.Metadata.IsLazy
                   && (_mpf.Metadata.PendingParts?.Length ?? 0) > 0)
            {
                var before = _mpf.Metadata.PendingParts?.Length ?? 0;
                var meta = await _resolver!
                    .ResolveNextAsync(_mpf, CancellationToken.None)
                    .ConfigureAwait(false);
                _mpf.Metadata = meta;
                if ((meta.PendingParts?.Length ?? 0) >= before) break;
            }
        }
        catch (Exception e)
        {
            Log.Debug(e,
                "Background RAR pre-warm for {Id} did not finish; trailing volumes will resolve on demand.",
                _mpf.Id);
        }
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_position >= _length) return 0;
        _innerStream ??= await GetFileStreamAsync(_position, cancellationToken).ConfigureAwait(false);
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
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

    public override void SetLength(long value)
    {
        throw new InvalidOperationException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    // Walks resolved FileParts + pending estimates so HEAD/Length-aware
    // clients see the stable inner-file size from the moment of mount. The
    // estimates are adjusted at import time so this matches the real
    // uncompressed size byte-exact.
    // Old MemoryPack blobs predate the lazy fields, so PendingParts can be
    // null after deserialization despite the property initializer. Guard
    // every iteration with ?? [] to stay safe.
    private static long ComputeLength(DavMultipartFile.Meta meta)
    {
        var pendingParts = meta.PendingParts ?? [];
        if (meta.AesParams != null && meta.IsLazy && pendingParts.Length > 0)
        {
            // The lazy estimates describe the decoded file and therefore do
            // not include the final AES block padding. The decoder validates
            // the packed stream length before any trailing RAR headers have
            // been resolved, so expose the deterministic padded length until
            // the exact part map replaces the estimates.
            return AesDecoderStream.GetCiphertextLength(meta.AesParams.DecodedSize);
        }

        var sum = 0L;
        foreach (var p in meta.FileParts ?? []) sum += p.FilePartByteRange.Count;
        foreach (var p in pendingParts) sum += p.EstimatedDataSize;
        foreach (var p in meta.TailFileParts ?? []) sum += p.FilePartByteRange.Count;
        return sum;
    }

    private readonly record struct ResolvedPosition(
        DavMultipartFile.FilePart[] Parts,
        int PartIndex,
        long PartOffset,
        bool IsTail);

    private ResolvedPosition SeekFilePart(
        DavMultipartFile.Meta meta,
        long byteOffset)
    {
        long offset = 0;
        var fileParts = meta.FileParts ?? [];
        for (var i = 0; i < fileParts.Length; i++)
        {
            var filePart = fileParts[i];
            var nextOffset = offset + filePart.FilePartByteRange.Count;
            if (byteOffset < nextOffset)
                return new ResolvedPosition(fileParts, i, offset, IsTail: false);
            offset = nextOffset;
        }

        var tailParts = meta.TailFileParts ?? [];
        var tailBytes = tailParts.Sum(part => part.FilePartByteRange.Count);
        offset = _length - tailBytes;
        for (var i = 0; i < tailParts.Length; i++)
        {
            var nextOffset = offset + tailParts[i].FilePartByteRange.Count;
            if (byteOffset < nextOffset)
                return new ResolvedPosition(tailParts, i, offset, IsTail: true);
            offset = nextOffset;
        }

        throw new SeekPositionNotFoundException($"Corrupt file. Cannot seek to byte position {byteOffset}.");
    }

    private async Task<CombinedStream> GetFileStreamAsync(long rangeStart, CancellationToken ct)
    {
        // Resolve only enough trailing volumes to cover the requested offset —
        // no waiting on the background pre-warm. For byte 0 that's nothing (the
        // first volume is resolved at import), so playback starts immediately.
        // A seek into a not-yet-resolved volume resolves the gap up to it here,
        // sharing in-flight work with the pre-warm via the resolver, so the
        // player only ever waits for volumes up to where it actually jumped —
        // never the whole archive.
        var meta = await EnsureCoveringAsync(rangeStart, ct).ConfigureAwait(false);

        if (rangeStart == 0)
            return new CombinedStream(EnumerateFromPart(0, 0, ct));

        var resolved = SeekFilePart(meta, rangeStart);
        var firstOffset = rangeStart - resolved.PartOffset;
        return resolved.IsTail
            ? new CombinedStream(EnumerateResolvedParts(resolved.Parts, resolved.PartIndex, firstOffset))
            : new CombinedStream(EnumerateFromPart(resolved.PartIndex, firstOffset, ct));
    }

    // Resolve trailing volumes up to (and including) the one that contains
    // `byteOffset` so SeekFilePart can map the offset to an exact slot.
    // No-op for non-lazy archives.
    private async Task<DavMultipartFile.Meta> EnsureCoveringAsync(long byteOffset, CancellationToken ct)
    {
        if (_resolver is null) return _mpf.Metadata;
        var meta = await _resolver
            .EnsureResolvedForReadAsync(_mpf, byteOffset, _length, ct)
            .ConfigureAwait(false);
        _mpf.Metadata = meta;
        return meta;
    }

    private IEnumerable<Task<Stream>> EnumerateResolvedParts(
        DavMultipartFile.FilePart[] parts,
        int firstPartIndex,
        long firstOffset)
    {
        for (var i = firstPartIndex; i < parts.Length; i++)
        {
            var extraOffset = i == firstPartIndex ? firstOffset : 0;
            yield return Task.FromResult(OpenPart(parts[i], extraOffset));
        }
    }

    // Lazy iterator over the file's volume sequence. Each yielded Task opens
    // one volume's segment range. When we run out of resolved FileParts but
    // PendingParts remain, the next yield triggers lazy resolution before
    // opening — so the player keeps streaming across volume boundaries
    // without having paid for them at mount time.
    private IEnumerable<Task<Stream>> EnumerateFromPart(int firstFilePartIndex, long firstOffset, CancellationToken ct)
    {
        var i = firstFilePartIndex;
        while (true)
        {
            var meta = _mpf.Metadata;
            var fileParts = meta.FileParts ?? [];
            if (i < fileParts.Length)
            {
                var part = fileParts[i];
                var extraOffset = (i == firstFilePartIndex) ? firstOffset : 0;
                yield return Task.FromResult(OpenPart(part, extraOffset));
                i++;
                continue;
            }

            if (_resolver != null && meta.IsLazy && (meta.PendingParts?.Length ?? 0) > 0)
            {
                yield return ResolveAndOpenAsync(i, ct);
                i++;
                continue;
            }

            yield break;
        }
    }

    private Stream OpenPart(DavMultipartFile.FilePart part, long extraOffset)
    {
        if (_partOpener != null) return _partOpener(part, extraOffset);
        var stream = _usenetClient.GetFileStream(part.SegmentIds, part.SegmentIdByteRange.Count, _readAheadBytes);
        stream.Seek(part.FilePartByteRange.StartInclusive + extraOffset, SeekOrigin.Begin);
        return stream.LimitLength(part.FilePartByteRange.Count - extraOffset);
    }

    private async Task<Stream> ResolveAndOpenAsync(int targetIndex, CancellationToken ct)
    {
        var meta = await _resolver!.ResolveNextAsync(_mpf, ct).ConfigureAwait(false);
        _mpf.Metadata = meta;
        if (targetIndex >= meta.FileParts.Length)
        {
            // Resolver should always grow FileParts when there were pending
            // parts. If we land here, treat as EOF — CombinedStream advances
            // to the next yield (which will hit yield break).
            return new MemoryStream(Array.Empty<byte>(), writable: false);
        }
        return OpenPart(meta.FileParts[targetIndex], 0);
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
