using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

// A read-only in-memory prefix whose logical position is a long. MemoryStream
// limits Position to int.MaxValue even when no data is allocated there. RAR
// parsers seek past a stored entry before yielding its header, so a perfectly
// valid >2 GiB entry cannot be inspected from a short MemoryStream prefix.
internal sealed class LongSeekablePrefixStream(byte[] prefix) : FastReadOnlyStream
{
    private long _position;

    public override bool CanSeek => true;
    public override long Length => prefix.LongLength;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_position >= prefix.LongLength || buffer.IsEmpty) return ValueTask.FromResult(0);

        var count = (int)Math.Min(buffer.Length, prefix.LongLength - _position);
        prefix.AsMemory((int)_position, count).CopyTo(buffer);
        _position += count;
        return ValueTask.FromResult(count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = position;
        return _position;
    }
}
