using NzbWebDAV.Exceptions;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Keeps a cancellation-link lifetime attached to a lazily consumed yEnc body.
/// The lifetime is released with the stream, after the underlying NNTP body has
/// been disposed and its connection callback has had a chance to run.
///
/// When an inactivity timeout is supplied, the stream also enforces progress on
/// the body transfer. The provider attempt deadline is disarmed once a body
/// response returns, so without this a socket that stops delivering bytes
/// mid-body pins the read for as long as it stays open.
/// </summary>
internal sealed class LifetimeYencStream : YencStream
{
    private readonly YencStream _inner;
    private readonly IDisposable _lifetime;
    private readonly Action? _onStall;
    private readonly Action? _onCompleted;
    private readonly Action<Exception, long>? _onFailure;
    private readonly string? _providerId;
    private readonly string? _providerHost;
    private int _disposed;
    private int _lifetimeReleased;
    private int _terminalReported;

    public LifetimeYencStream(
        YencStream inner,
        IDisposable lifetime,
        TimeSpan? inactivityTimeout = null,
        Action? onStall = null,
        Action? onCompleted = null,
        Action<Exception, long>? onFailure = null,
        string? providerId = null,
        string? providerHost = null) : base(Stream.Null)
    {
        _inner = inner;
        _lifetime = lifetime;
        _onStall = onStall;
        _onCompleted = onCompleted;
        _onFailure = onFailure;
        _providerId = providerId;
        _providerHost = providerHost;
        // Armed on the decoder, not here: one read of this stream can pull many
        // reads from the socket, so a deadline at this level would measure how
        // long a whole buffer takes to fill rather than how long the transfer
        // has been silent.
        if (inactivityTimeout is not null) inner.ArmReadInactivityWatchdog(inactivityTimeout.Value);
    }

    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
        CancellationToken cancellationToken = default) =>
        _inner.GetYencHeadersAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int read;
        try
        {
            read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (ReadInactivityTimeoutException e)
        {
            // Cancel the attempt so the connection stops waiting on a socket that
            // has gone quiet. Its background body pump then reports the body as
            // not retrieved, which is what replaces the connection.
            _onStall?.Invoke();
            var stalled = new BodyProgressStalledException(
                $"The article body delivered {_inner.DecodedBytes} bytes and then stopped.",
                _inner.DecodedBytes,
                _providerId,
                _providerHost,
                e);
            ReportFailure(stalled);
            throw stalled;
        }
        catch (Exception e)
        {
            // A player closing its range request is not a failed provider body.
            // The request's terminal diagnostics account for the abort.
            if (e is not OperationCanceledException)
                ReportFailure(e);
            throw;
        }

        if (read == 0)
        {
            if (Interlocked.Exchange(ref _terminalReported, 1) == 0)
                _onCompleted?.Invoke();
            ReleaseLifetime();
        }
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _inner.Dispose();
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
            _lifetime.Dispose();
    }

    private void ReportFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _terminalReported, 1) == 0)
            _onFailure?.Invoke(exception, _inner.DecodedBytes);
    }
}
