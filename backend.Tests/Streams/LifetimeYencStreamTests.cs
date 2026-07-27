using System.Text;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

public class LifetimeYencStreamTests
{
    private const int LineLength = 128;

    [Fact]
    public async Task StalledBodyThrowsAndCancelsTheAttempt()
    {
        var stalled = false;
        var source = new ScriptedStream(Article(dataLines: 4), LineLength + 2, stallAfterChunk: 2);
        await using var stream = Wrap(
            source,
            TimeSpan.FromMilliseconds(200),
            () => stalled = true,
            providerId: "provider-1",
            providerHost: "news.example");

        Assert.True(await stream.ReadAsync(new byte[64]) > 0);
        var exception = await Assert.ThrowsAsync<BodyProgressStalledException>(async () =>
            await stream.CopyToAsync(new MemoryStream()));

        Assert.True(stalled);
        Assert.True(exception.TransferredBytes > 0);
        Assert.Equal("provider-1", exception.ProviderId);
        Assert.Equal("news.example", exception.ProviderHost);
    }

    [Fact]
    public async Task SlowButProgressingBodyIsLeftAlone()
    {
        // Six chunks at 60ms each run well past the 200ms deadline in total, so
        // this only passes if the deadline is per-read rather than per-body.
        var source = new ScriptedStream(
            Article(dataLines: 6), LineLength + 2, chunkDelay: TimeSpan.FromMilliseconds(60));
        await using var stream = Wrap(source, TimeSpan.FromMilliseconds(200));
        var decoded = new MemoryStream();

        await stream.CopyToAsync(decoded);

        Assert.Equal(6 * LineLength, decoded.Length);
    }

    [Fact]
    public async Task WatchdogReachesTheDecoderThroughTheProviderByteCounter()
    {
        var source = new ScriptedStream(
            Article(dataLines: 4),
            LineLength + 2,
            stallAfterChunk: 2);
        var tracker = new ProviderBytesTracker();
        var counted = new CountingYencStream(
            new YencStream(source),
            tracker,
            "provider-1");
        await using var stream = new LifetimeYencStream(
            counted,
            new Releaser(() => { }),
            TimeSpan.FromMilliseconds(200),
            providerId: "provider-1",
            providerHost: "news.example");

        Assert.True(await stream.ReadAsync(new byte[64]) > 0);
        var exception = await Assert.ThrowsAsync<BodyProgressStalledException>(async () =>
            await stream.CopyToAsync(new MemoryStream()));

        Assert.True(exception.TransferredBytes > 0);
        Assert.True(tracker.GetLifetime("provider-1") > 0);
    }

    [Fact]
    public async Task ReleasesItsLifetimeWhenTheBodyCompletes()
    {
        var released = false;
        var source = new ScriptedStream(Article(dataLines: 2), LineLength + 2);
        await using var stream = new LifetimeYencStream(
            new YencStream(source),
            new Releaser(() => released = true),
            TimeSpan.FromSeconds(5));

        await stream.CopyToAsync(new MemoryStream());

        Assert.True(released);
    }

    [Fact]
    public async Task ReportsProviderSuccessOnlyAfterTheBodyReachesEof()
    {
        var completed = 0;
        var failed = 0;
        var source = new ScriptedStream(Article(dataLines: 2), LineLength + 2);
        await using var stream = new LifetimeYencStream(
            new YencStream(source),
            new Releaser(() => { }),
            TimeSpan.FromSeconds(5),
            onCompleted: () => completed++,
            onFailure: (_, _) => failed++);
        var decoded = new MemoryStream();

        Assert.Equal(0, completed);
        await stream.CopyToAsync(decoded);

        Assert.Equal(2 * LineLength, decoded.Length);
        Assert.Equal(1, completed);
        Assert.Equal(0, failed);
    }

    [Fact]
    public async Task PlayerCancellationDoesNotReportAProviderBodyFailure()
    {
        var failed = 0;
        using var abort = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var source = new ScriptedStream(
            Article(dataLines: 2),
            LineLength + 2,
            stallAfterChunk: 0);
        await using var stream = new LifetimeYencStream(
            new YencStream(source),
            new Releaser(() => { }),
            TimeSpan.FromSeconds(5),
            onFailure: (_, _) => failed++);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await stream.CopyToAsync(new MemoryStream(), abort.Token));

        Assert.Equal(0, failed);
    }

    private static LifetimeYencStream Wrap(
        Stream source,
        TimeSpan inactivity,
        Action? onStall = null,
        string? providerId = null,
        string? providerHost = null) =>
        new(
            new YencStream(source),
            new Releaser(() => { }),
            inactivity,
            onStall,
            providerId: providerId,
            providerHost: providerHost);

    private static byte[] Article(int dataLines)
    {
        var size = dataLines * LineLength;
        var body = new StringBuilder();
        body.Append($"=ybegin line={LineLength} size={size} name=test.bin\r\n");
        for (var i = 0; i < dataLines; i++)
            body.Append(new string('k', LineLength)).Append("\r\n"); // 'A' + yEnc's 42-byte offset
        body.Append($"=yend size={size}\r\n");
        return Encoding.Latin1.GetBytes(body.ToString());
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>
    /// Hands out the article in fixed-size chunks, optionally pausing between
    /// them, and optionally going silent forever after a given chunk.
    /// </summary>
    private sealed class ScriptedStream(
        byte[] data,
        int chunkSize,
        TimeSpan chunkDelay = default,
        int stallAfterChunk = -1) : Stream
    {
        private int _position;
        private int _chunks;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stallAfterChunk >= 0 && _chunks >= stallAfterChunk)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            if (chunkDelay > TimeSpan.Zero)
                await Task.Delay(chunkDelay, cancellationToken);

            var count = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _position);
            if (count <= 0) return 0;
            data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            _chunks++;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
