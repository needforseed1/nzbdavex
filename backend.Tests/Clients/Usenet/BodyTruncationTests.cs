using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Exceptions;

namespace NzbWebDAV.Tests.Clients.Usenet;

/// <summary>
/// A body that stops early must surface as a fault. Completing the pipe cleanly
/// would hand the caller a short segment that looks successful, which silently
/// shifts every later byte of the file it belongs to.
/// </summary>
public class BodyTruncationTests
{
    private const int LineLength = 128;

    [Fact]
    public async Task ReadingAnInterruptedBodyThrows()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // Two data lines, then the socket drops: no trailer, no terminating dot.
        var server = RunServerAsync(listener, dataLines: 2, complete: false, timeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, timeout.Token);
        await client.AuthenticateAsync("user", "pass", timeout.Token);
        var response = await client.DecodedBodyAsync("a", timeout.Token);

        // The pipe carries the interruption itself, so the reader sees the
        // protocol failure rather than the decoder's later "no =yend" verdict.
        await Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await response.Stream!.CopyToAsync(new MemoryStream(), timeout.Token));
        await server;
    }

    [Fact]
    public async Task ReadingACompleteBodySucceeds()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunServerAsync(listener, dataLines: 2, complete: true, timeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, timeout.Token);
        await client.AuthenticateAsync("user", "pass", timeout.Token);
        var response = await client.DecodedBodyAsync("a", timeout.Token);
        var decoded = new MemoryStream();

        await response.Stream!.CopyToAsync(decoded, timeout.Token);

        Assert.Equal(2 * LineLength, decoded.Length);
        await server;
    }

    [Fact]
    public async Task PipelinedBodyThatStopsOnTheWireTimesOut()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stopServer = new CancellationTokenSource();
        var server = RunStallingPipelinedServerAsync(listener, stopServer.Token);

        try
        {
            using var client = new BaseNntpClient();
            await client.ConnectAsync("127.0.0.1", port, false, testTimeout.Token);
            await client.AuthenticateAsync("user", "pass", testTimeout.Token);
            using var bodyDeadline =
                ContextualCancellationTokenSource.CreateLinkedTokenSource(testTimeout.Token);
            bodyDeadline.SetContext(
                new BodyReadInactivityContext(TimeSpan.FromMilliseconds(150)));

            await using var bodies = client
                .DecodedBodiesPipelinedAsync(
                    ["a"], depth: 1, cancellationToken: bodyDeadline.Token)
                .GetAsyncEnumerator(bodyDeadline.Token);
            var exception = await Assert.ThrowsAsync<ReadInactivityTimeoutException>(
                () => bodies.MoveNextAsync().AsTask());

            Assert.True(exception.TransferredBytes > 0);
        }
        finally
        {
            await stopServer.CancelAsync();
            await server;
        }
    }

    [Fact]
    public async Task PipelinedBodyCanTakeLongerThanDeadlineWhileItKeepsProgressing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunProgressingPipelinedServerAsync(listener, testTimeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, testTimeout.Token);
        await client.AuthenticateAsync("user", "pass", testTimeout.Token);
        using var bodyDeadline =
            ContextualCancellationTokenSource.CreateLinkedTokenSource(testTimeout.Token);
        bodyDeadline.SetContext(
            new BodyReadInactivityContext(TimeSpan.FromMilliseconds(180)));

        PipelinedBodyResult? result = null;
        await foreach (var body in client.DecodedBodiesPipelinedAsync(
                           ["a"], depth: 1, cancellationToken: bodyDeadline.Token))
            result = body;

        var decoded = new MemoryStream();
        await result!.Stream!.CopyToAsync(decoded, testTimeout.Token);

        Assert.Equal(4 * LineLength, decoded.Length);
        await server;
    }

    private static async Task RunServerAsync(
        TcpListener listener,
        int dataLines,
        bool complete,
        CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("200 ready");
        Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("281 authenticated");

        Assert.Equal("BODY <a>", await reader.ReadLineAsync(cancellationToken));
        var size = dataLines * LineLength;
        await writer.WriteLineAsync("222 0 <a> body follows");
        await writer.WriteLineAsync($"=ybegin line={LineLength} size={size} name=test.bin");
        for (var i = 0; i < dataLines; i++)
            await writer.WriteLineAsync(new string('k', LineLength)); // 'A' + yEnc's 42-byte offset

        if (!complete) return;

        await writer.WriteLineAsync($"=yend size={size}");
        await writer.WriteLineAsync(".");
    }

    private static async Task RunStallingPipelinedServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
            await using var writer =
                new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { AutoFlush = true };

            await AuthenticateAndReadBodyCommandAsync(reader, writer, cancellationToken);
            await writer.WriteLineAsync("222 0 <a> body follows");
            await writer.WriteLineAsync(
                $"=ybegin line={LineLength} size={2 * LineLength} name=test.bin");
            await writer.WriteLineAsync(new string('k', LineLength));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RunProgressingPipelinedServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
        await using var writer =
            new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { AutoFlush = true };

        await AuthenticateAndReadBodyCommandAsync(reader, writer, cancellationToken);
        await writer.WriteLineAsync("222 0 <a> body follows");
        await writer.WriteLineAsync(
            $"=ybegin line={LineLength} size={4 * LineLength} name=test.bin");
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken);
            await writer.WriteLineAsync(new string('k', LineLength));
        }
        await writer.WriteLineAsync($"=yend size={4 * LineLength}");
        await writer.WriteLineAsync(".");
    }

    private static async Task AuthenticateAndReadBodyCommandAsync(
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync("200 ready");
        Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("281 authenticated");
        Assert.Equal("BODY <a>", await reader.ReadLineAsync(cancellationToken));
    }
}
