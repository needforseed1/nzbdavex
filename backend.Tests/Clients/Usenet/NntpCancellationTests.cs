using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Clients.Usenet;

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
}
