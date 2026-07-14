using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class DrainingNntpClientTests
{
    [Fact]
    public async Task ReplacementRoutesNewCallsWhileExistingCallFinishesOnOldClient()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldClient = new TrackingClient(async ct =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Response();
        });
        var newClient = new TrackingClient(_ => Task.FromResult(Response()));
        using var client = new DrainingNntpClient(oldClient);

        var existingCall = client.DateAsync(CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        client.Replace(newClient);
        Assert.False(oldClient.Disposed);
        await client.DateAsync(CancellationToken.None);
        Assert.Equal(1, newClient.DateCalls);

        release.TrySetResult();
        await existingCall;
        Assert.True(oldClient.Disposed);
        Assert.False(newClient.Disposed);
    }

    [Fact]
    public void ReplacementDisposesIdleOldGraphImmediately()
    {
        var oldClient = new TrackingClient(_ => Task.FromResult(Response()));
        var newClient = new TrackingClient(_ => Task.FromResult(Response()));
        var client = new DrainingNntpClient(oldClient);

        client.Replace(newClient);
        Assert.True(oldClient.Disposed);
        Assert.False(newClient.Disposed);

        client.Dispose();
        Assert.True(newClient.Disposed);
    }

    [Fact]
    public async Task ExclusiveOperationStaysOnOldGraphUntilItsConnectionIsReleased()
    {
        var oldClient = new TrackingClient(_ => Task.FromResult(Response()));
        var newClient = new TrackingClient(_ => Task.FromResult(Response()));
        using var client = new DrainingNntpClient(oldClient);

        var exclusive = await client.AcquireExclusiveConnectionAsync("segment", CancellationToken.None);
        client.Replace(newClient);

        var response = await client.DecodedBodyAsync("segment", exclusive, CancellationToken.None);
        Assert.Equal(1, oldClient.ExclusiveBodyCalls);
        Assert.Equal(0, newClient.ExclusiveBodyCalls);
        Assert.False(oldClient.Disposed);

        oldClient.ReleaseExclusiveConnection();
        Assert.True(oldClient.PermitReleased);
        Assert.True(oldClient.Disposed);
        await response.Stream.DisposeAsync();
    }

    [Fact]
    public async Task StreamedBodyKeepsOldGraphAliveUntilItsConnectionIsReleased()
    {
        var oldClient = new TrackingClient(_ => Task.FromResult(Response()));
        var newClient = new TrackingClient(_ => Task.FromResult(Response()));
        using var client = new DrainingNntpClient(oldClient);

        var response = await client.DecodedBodyAsync("segment", CancellationToken.None);
        client.Replace(newClient);

        Assert.Equal(1, oldClient.BodyCalls);
        Assert.False(oldClient.Disposed);

        oldClient.ReleaseBodyConnection();
        Assert.True(oldClient.Disposed);
        await response.Stream.DisposeAsync();
    }

    [Fact]
    public void ReplacementAfterDisposalDisposesRejectedGraph()
    {
        var current = new TrackingClient(_ => Task.FromResult(Response()));
        var rejected = new TrackingClient(_ => Task.FromResult(Response()));
        var client = new DrainingNntpClient(current);
        client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => client.Replace(rejected));
        Assert.True(rejected.Disposed);
    }

    private static UsenetDateResponse Response() => new()
    {
        ResponseCode = 111,
        ResponseMessage = "date"
    };

    private sealed class TrackingClient(
        Func<CancellationToken, Task<UsenetDateResponse>> date) : NntpClient
    {
        public int DateCalls { get; private set; }
        public int BodyCalls { get; private set; }
        public int ExclusiveBodyCalls { get; private set; }
        public bool Disposed { get; private set; }
        public bool PermitReleased { get; private set; }
        private Action<ArticleBodyResult>? _releaseExclusiveConnection;
        private Action<ArticleBodyResult>? _releaseBodyConnection;

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
        {
            DateCalls++;
            return date(cancellationToken);
        }

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            BodyCalls++;
            _releaseBodyConnection = onConnectionReadyAgain;
            return Task.FromResult(BodyResponse(segmentId));
        }

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId, CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(_ => PermitReleased = true));

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken)
        {
            ExclusiveBodyCalls++;
            _releaseExclusiveConnection = exclusiveConnection.OnConnectionReadyAgain;
            return Task.FromResult(BodyResponse(segmentId));
        }

        public void ReleaseExclusiveConnection()
        {
            _releaseExclusiveConnection?.Invoke(ArticleBodyResult.Retrieved);
        }

        public void ReleaseBodyConnection()
        {
            _releaseBodyConnection?.Invoke(ArticleBodyResult.Retrieved);
        }

        private static UsenetDecodedBodyResponse BodyResponse(string segmentId) => new()
        {
            ResponseCode = 222,
            ResponseMessage = "body",
            SegmentId = segmentId,
            Stream = new YencStream(new MemoryStream())
        };

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            Action<ArticleBodyResult>? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
            Disposed = true;
        }
    }
}
