using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class NntpAuthenticationTests
{
    [Fact]
    public async Task TooManyConnectionsResponseIsClassifiedAsConnectionLimit()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunConnectionLimitServerAsync(listener, timeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, timeout.Token);

        var failure = await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
            () => client.AuthenticateAsync("user", "pass", timeout.Token));

        Assert.IsType<UsenetConnectionLimitException>(failure.InnerException);
        await server;
    }

    private static async Task RunConnectionLimitServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true)
        {
            AutoFlush = true,
        };

        await writer.WriteLineAsync("200 ready");
        Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("502 Too many connections.");
    }
}
