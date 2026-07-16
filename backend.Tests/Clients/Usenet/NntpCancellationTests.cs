using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Config;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class NntpCancellationTests
{
    [Fact]
    public async Task StatCancellationInterruptsSocketRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunStallingServerAsync(listener, serverCts.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, serverCts.Token);
        await client.AuthenticateAsync("user", "pass", serverCts.Token);

        using var commandCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.StatAsync("article", commandCts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Cancellation took {stopwatch.Elapsed}.");
        serverCts.Cancel();
        await server;
    }

    [Fact]
    public async Task ConnectCancellationAbortsStalledTlsHandshake()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var accepted = AcceptAndStallAsync(listener, serverCts.Token);

        using var client = new BaseNntpClient();
        using var connectCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ConnectAsync("127.0.0.1", port, true, connectCts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"TLS cancellation took {stopwatch.Elapsed}.");
        await serverCts.CancelAsync();
        await accepted;
    }

    [Fact]
    public async Task ProviderConnectionSetupHasAnOverallDeadline()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunAuthenticationStallingServerAsync(listener, serverCts.Token);
        var details = new UsenetProviderConfig.ConnectionDetails
        {
            Type = ProviderType.Pooled,
            Host = "127.0.0.1",
            Port = port,
            UseSsl = false,
            User = "user",
            Pass = "pass",
            MaxConnections = 1,
        };
        var budget = new ConnectionLifetimeBudget(1, 1);
        await using var stalledPool = new ConnectionPool<INntpClient>(
            1,
            ct => UsenetStreamingClient.CreateNewConnection(
                details, TimeSpan.FromMilliseconds(100), ct),
            connectionBudget: budget);

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await stalledPool.GetConnectionLockAsync(SemaphorePriority.Low));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Connection setup timeout took {stopwatch.Elapsed}.");
        Assert.Equal(0, budget.ActiveCreations);
        Assert.Equal(0, budget.ReservedConnections);

        await using var recoveryPool = new ConnectionPool<DisposableMarker>(
            1,
            _ => ValueTask.FromResult(new DisposableMarker()),
            connectionBudget: budget);
        using var recovered = await recoveryPool
            .GetConnectionLockAsync(SemaphorePriority.Low)
            .WaitAsync(TimeSpan.FromSeconds(1));

        await serverCts.CancelAsync();
        await server;
    }

    private static async Task RunStallingServerAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync("200 ready");
            Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync(ct));
            await writer.WriteLineAsync("281 authenticated");
            Assert.StartsWith("STAT", await reader.ReadLineAsync(ct));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task AcceptAndStallAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task RunAuthenticationStallingServerAsync(
        TcpListener listener,
        CancellationToken ct)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true)
            {
                AutoFlush = true
            };

            await writer.WriteLineAsync("200 ready");
            Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync(ct));
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed class DisposableMarker : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
