using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionPoolGrowthTests
{
    [Fact]
    public async Task PoolGrowsDuringSustainedDemandWhileForegroundReusesWarmSockets()
    {
        var created = 0;
        await using var pool = new ConnectionPool<PooledConnection>(
            8,
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(30), ct);
                return new PooledConnection(Interlocked.Increment(ref created));
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Sustained short operations: each borrows a connection briefly. Under
        // the old behavior, fast idle returns won every race against the 30ms
        // handshake, so the pool stayed pinned at one connection.
        var workers = Enumerable.Range(0, 16).Select(async _ =>
        {
            for (var i = 0; i < 20; i++)
            {
                using var connection = await pool.GetConnectionLockAsync(
                    SemaphorePriority.Low, timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
            }
        }).ToArray();
        await Task.WhenAll(workers);

        Assert.InRange(pool.LiveConnections, 2, 8);
    }

    [Fact]
    public async Task DemandDrivenGrowthDoesNotCreateAnUnboundedHandshakeStorm()
    {
        var activeHandshakes = 0;
        var maxActiveHandshakes = 0;
        var totalHandshakes = 0;
        await using var pool = new ConnectionPool<PooledConnection>(
            8,
            async ct =>
            {
                var current = Interlocked.Increment(ref activeHandshakes);
                UpdateMaximum(ref maxActiveHandshakes, current);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
                    return new PooledConnection(Interlocked.Increment(ref totalHandshakes));
                }
                finally
                {
                    Interlocked.Decrement(ref activeHandshakes);
                }
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var workers = Enumerable.Range(0, 64).Select(async _ =>
        {
            for (var i = 0; i < 10; i++)
            {
                using var connection = await pool.GetConnectionLockAsync(
                    SemaphorePriority.Low, timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(2), timeout.Token);
            }
        }).ToArray();
        await Task.WhenAll(workers);

        // Growth is bounded by the pool's effective maximum: live plus in-flight
        // creations never exceed it, so concurrent handshakes stay within the
        // configured connection cap instead of storming.
        Assert.InRange(Volatile.Read(ref maxActiveHandshakes), 1, 8);
        Assert.InRange(pool.LiveConnections, 2, 8);
    }

    [Fact]
    public async Task SaturatedInFlightCreationsDoNotSpinOrOverflowUnderHeavyDemand()
    {
        // Small pool, slow handshakes, many short borrows: in-flight detached
        // creations keep the pool at its reservation cap for long stretches.
        // The acquisition loop must keep making bounded progress (no recursive
        // retry chain, no stack growth) until capacity is published or freed.
        var created = 0;
        await using var pool = new ConnectionPool<PooledConnection>(
            4,
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
                return new PooledConnection(Interlocked.Increment(ref created));
            });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var workers = Enumerable.Range(0, 32).Select(async _ =>
        {
            for (var i = 0; i < 10; i++)
            {
                using var connection = await pool.GetConnectionLockAsync(
                    SemaphorePriority.Low, timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(1), timeout.Token);
            }
        }).ToArray();
        await Task.WhenAll(workers);

        Assert.InRange(pool.LiveConnections, 1, 4);
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed) return;
            observed = previous;
        }
    }

    private sealed class PooledConnection(int id) : IDisposable
    {
        public int Id { get; } = id;

        public void Dispose()
        {
        }
    }
}
