using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Keeps a cancellation-link lifetime attached to a lazily consumed yEnc body.
/// The lifetime is released with the stream, after the underlying NNTP body has
/// been disposed and its connection callback has had a chance to run.
/// </summary>
internal sealed class LifetimeYencStream(YencStream inner, IDisposable lifetime) : YencStream(Stream.Null)
{
    private int _disposed;
    private int _lifetimeReleased;

    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
        CancellationToken cancellationToken = default) =>
        inner.GetYencHeadersAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read == 0) ReleaseLifetime();
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                inner.Dispose();
            }
            finally
            {
                ReleaseLifetime();
            }
        }

        base.Dispose(disposing);
    }

    private void ReleaseLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeReleased, 1) == 0)
            lifetime.Dispose();
    }
}
