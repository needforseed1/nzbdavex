using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class CancellableStreamTests
{
    [Fact]
    public async Task BoundTokenCancelsReadWhenCallerPassesNone()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var inner = new TokenObservingStream();
        await using var stream = new CancellableStream(inner, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.ReadAsync(new byte[1], CancellationToken.None).AsTask());
        Assert.Equal(cts.Token, inner.ObservedToken);
    }

    private sealed class TokenObservingStream : MemoryStream
    {
        public CancellationToken ObservedToken { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            return cancellationToken.IsCancellationRequested
                ? ValueTask.FromCanceled<int>(cancellationToken)
                : ValueTask.FromResult(0);
        }
    }
}
