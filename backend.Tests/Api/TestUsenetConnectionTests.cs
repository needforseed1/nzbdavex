using NzbWebDAV.Api.Controllers.TestUsenetConnection;

namespace NzbWebDAV.Tests.Api;

public class TestUsenetConnectionTests
{
    [Fact]
    public async Task SuccessfulTemporaryConnectionIsDisposed()
    {
        var connection = new TrackingDisposable();

        await TestUsenetConnectionController.ConnectAndDisposeAsync(
            _ => ValueTask.FromResult(connection), CancellationToken.None);

        Assert.True(connection.Disposed);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
