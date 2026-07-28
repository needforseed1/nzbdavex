using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Exceptions;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class BulkStatTransportTests
{
    [Fact]
    public async Task BulkStatMapsResponsesInCommandOrder()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunServerAsync(listener, ["a", "b", "c"], [
            "223 0 <a>",
            "430 no article",
            "223 0 <c>",
        ], timeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, timeout.Token);
        await client.AuthenticateAsync("user", "pass", timeout.Token);
        var results = await CollectAsync(client.StatsPipelinedAsync(["a", "b", "c"], 3, timeout.Token));

        Assert.Equal([true, false, true], results.Select(result => result.Exists));
        await server;
    }

    [Fact]
    public async Task BulkStatRejectsOutOfOrderResponseEcho()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunServerAsync(listener, ["a", "b"], [
            "223 0 <b>",
            "223 0 <a>",
        ], timeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, timeout.Token);
        await client.AuthenticateAsync("user", "pass", timeout.Token);

        await Assert.ThrowsAsync<UsenetProtocolException>(async () =>
            await CollectAsync(client.StatsPipelinedAsync(["a", "b"], 2, timeout.Token)));
        await server;
    }

    [Fact]
    public async Task BulkStatBuffersResponsesUntilTheBatchCompletes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var releaseSecondResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunIncrementalServerAsync(
            listener, releaseSecondResponse.Task, timeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, timeout.Token);
        await client.AuthenticateAsync("user", "pass", timeout.Token);
        await using var results = client
            .StatsPipelinedAsync(["a", "b"], 2, timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        var firstResult = results.MoveNextAsync().AsTask();
        await Task.Delay(50, timeout.Token);
        Assert.False(firstResult.IsCompleted);
        releaseSecondResponse.TrySetResult();

        Assert.True(await firstResult);
        Assert.Equal("a", results.Current.SegmentId);
        Assert.True(results.Current.Exists);
        Assert.True(await results.MoveNextAsync());
        Assert.Equal("b", results.Current.SegmentId);
        Assert.True(results.Current.Exists);
        Assert.False(await results.MoveNextAsync());
        await server;
    }

    [Fact]
    public async Task AbandonedBufferedBatchCanReuseConnectionAfterRepliesAreDrained()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var releaseSecondResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = RunIncrementalServerAsync(
            listener, releaseSecondResponse.Task, timeout.Token,
            expectLaterCommand: true);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, timeout.Token);
        await client.AuthenticateAsync("user", "pass", timeout.Token);
        var results = client
            .StatsPipelinedAsync(["a", "b"], 2, timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        var firstResult = results.MoveNextAsync().AsTask();
        await Task.Delay(50, timeout.Token);
        releaseSecondResponse.TrySetResult();
        Assert.True(await firstResult);
        await results.DisposeAsync();

        var later = await client.StatAsync("later-command", timeout.Token);
        Assert.True(later.ArticleExists);
        await server;
    }

    [Fact]
    public async Task BulkStatCancellationInterruptsResponseRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunStallingServerAsync(listener, ["a", "b"], serverTimeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, serverTimeout.Token);
        await client.AuthenticateAsync("user", "pass", serverTimeout.Token);
        using var commandTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(client.StatsPipelinedAsync(["a", "b"], 2, commandTimeout.Token)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Cancellation took {stopwatch.Elapsed}.");
        serverTimeout.Cancel();
        await server;
    }

    [Fact]
    public async Task BulkStatTimesOutWhenAResponseStalls()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunStallingServerAsync(listener, ["a", "b"], serverTimeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, serverTimeout.Token);
        await client.AuthenticateAsync("user", "pass", serverTimeout.Token);

        var stopwatch = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<UsenetPipelinedStatStalledException>(async () =>
            await CollectAsync(client.StatsPipelinedAsync(["a", "b"], 2, serverTimeout.Token)));
        stopwatch.Stop();

        Assert.Equal(0, failure.ReceivedResponses);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(4));
        serverTimeout.Cancel();
        await server;
    }

    [Fact]
    public async Task BulkStatYieldsCompletedResponsesBeforeReportingAStall()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var serverTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var server = RunPartiallyStallingServerAsync(listener, serverTimeout.Token);

        using var client = new BaseNntpClient();
        await client.ConnectAsync("127.0.0.1", port, false, serverTimeout.Token);
        await client.AuthenticateAsync("user", "pass", serverTimeout.Token);
        await using var results = client
            .StatsPipelinedAsync(["a", "b"], 2, serverTimeout.Token)
            .GetAsyncEnumerator(serverTimeout.Token);

        var stopwatch = Stopwatch.StartNew();
        Assert.True(await results.MoveNextAsync());
        Assert.Equal("a", results.Current.SegmentId);
        Assert.True(results.Current.Exists);

        var failure = await Assert.ThrowsAsync<UsenetPipelinedStatStalledException>(
            () => results.MoveNextAsync().AsTask());
        stopwatch.Stop();

        Assert.Equal(1, failure.ReceivedResponses);
        Assert.InRange(
            stopwatch.Elapsed,
            TimeSpan.FromSeconds(1.5),
            TimeSpan.FromSeconds(4));
        serverTimeout.Cancel();
        await server;
    }

    private static async Task RunServerAsync(
        TcpListener listener,
        IReadOnlyList<string> expectedIds,
        IReadOnlyList<string> responses,
        CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = socket.GetStream();
        using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("200 ready");
        Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync(cancellationToken));
        await writer.WriteLineAsync("281 authenticated");

        foreach (var expectedId in expectedIds)
            Assert.Equal($"STAT <{expectedId}>", await reader.ReadLineAsync(cancellationToken));
        foreach (var response in responses)
            await writer.WriteLineAsync(response);
    }

    private static async Task RunIncrementalServerAsync(
        TcpListener listener,
        Task releaseSecondResponse,
        CancellationToken cancellationToken,
        bool expectLaterCommand = false)
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
        await writer.WriteLineAsync("281 authenticated");
        Assert.Equal("STAT <a>", await reader.ReadLineAsync(cancellationToken));
        Assert.Equal("STAT <b>", await reader.ReadLineAsync(cancellationToken));

        await writer.WriteLineAsync("223 0 <a>");
        await releaseSecondResponse.WaitAsync(cancellationToken);
        await writer.WriteLineAsync("223 0 <b>");
        if (expectLaterCommand)
        {
            Assert.Equal("STAT <later-command>", await reader.ReadLineAsync(cancellationToken));
            await writer.WriteLineAsync("223 0 <later-command>");
        }
    }

    private static async Task RunStallingServerAsync(
        TcpListener listener,
        IReadOnlyList<string> expectedIds,
        CancellationToken cancellationToken)
    {
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync("200 ready");
            Assert.StartsWith("AUTHINFO USER", await reader.ReadLineAsync(cancellationToken));
            await writer.WriteLineAsync("281 authenticated");
            foreach (var expectedId in expectedIds)
                Assert.Equal($"STAT <{expectedId}>", await reader.ReadLineAsync(cancellationToken));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task RunPartiallyStallingServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
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
            await writer.WriteLineAsync("281 authenticated");
            Assert.Equal("STAT <a>", await reader.ReadLineAsync(cancellationToken));
            Assert.Equal("STAT <b>", await reader.ReadLineAsync(cancellationToken));
            await writer.WriteLineAsync("223 0 <a>");
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<List<PipelinedStatResult>> CollectAsync(
        IAsyncEnumerable<PipelinedStatResult> source)
    {
        var results = new List<PipelinedStatResult>();
        await foreach (var result in source) results.Add(result);
        return results;
    }
}
