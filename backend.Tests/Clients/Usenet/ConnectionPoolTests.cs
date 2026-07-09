using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolTests
{
    [Fact]
    public async Task ValidationDiscardsUnhealthyIdleConnectionsWithoutExceedingCapacity()
    {
        var created = 0;
        var disposed = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            2,
            _ => ValueTask.FromResult(new TrackedConnection(
                Interlocked.Increment(ref created), () => Interlocked.Increment(ref disposed))),
            idleTimeout: TimeSpan.FromMinutes(1),
            connectionValidator: (connection, _) => ValueTask.FromResult(connection.Healthy),
            validateAfterIdle: TimeSpan.Zero);

        using (var first = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        using (var second = await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        {
            first.Connection.Healthy = false;
            second.Connection.Healthy = false;
        }

        using var replacement = await pool.GetConnectionLockAsync(SemaphorePriority.Low);
        Assert.True(replacement.Connection.Healthy);
        Assert.Equal(3, created);
        Assert.Equal(2, disposed);
        Assert.Equal(1, pool.LiveConnections);
    }

    [Fact]
    public async Task CloseIdleConnectionsReleasesCachedProviderSlots()
    {
        var disposed = 0;
        await using var pool = new ConnectionPool<TrackedConnection>(
            1,
            _ => ValueTask.FromResult(new TrackedConnection(1, () => Interlocked.Increment(ref disposed))));

        using (await pool.GetConnectionLockAsync(SemaphorePriority.Low))
        {
        }

        pool.CloseIdleConnections();
        Assert.Equal(1, disposed);
        Assert.Equal(0, pool.LiveConnections);
        Assert.Equal(0, pool.IdleConnections);
    }

    private sealed class TrackedConnection(int id, Action onDispose) : IDisposable
    {
        public int Id { get; } = id;
        public bool Healthy { get; set; } = true;
        public void Dispose() => onDispose();
    }
}
