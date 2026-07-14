using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// Atomically replaces an NNTP client graph while allowing work that already
/// entered the previous graph to finish before that graph is disposed.
/// </summary>
internal sealed class DrainingNntpClient : NntpClient, IQueueConnectionWarmer
{
    private readonly object _sync = new();
    private Generation _current;
    private bool _disposed;

    public DrainingNntpClient(INntpClient initialClient)
    {
        _current = new Generation(initialClient);
    }

    public void Replace(INntpClient replacement)
    {
        INntpClient? dispose = null;
        var rejected = false;
        lock (_sync)
        {
            if (_disposed)
                rejected = true;
            else
            {
                var previous = _current;
                _current = new Generation(replacement);
                previous.Retired = true;
                if (previous.ActiveCalls == 0) dispose = previous.Client;
            }
        }

        if (rejected)
        {
            SafeDispose(replacement);
            throw new ObjectDisposedException(nameof(DrainingNntpClient));
        }

        SafeDispose(dispose);
    }

    private Lease Acquire()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current.ActiveCalls++;
            return new Lease(this, _current);
        }
    }

    private void Release(Generation generation)
    {
        INntpClient? dispose = null;
        lock (_sync)
        {
            generation.ActiveCalls--;
            if (generation.Retired && generation.ActiveCalls == 0)
                dispose = generation.Client;
        }

        SafeDispose(dispose);
    }

    private async Task UseAsync(Func<INntpClient, Task> action)
    {
        using var lease = Acquire();
        await action(lease.Client).ConfigureAwait(false);
    }

    private async Task<T> UseAsync<T>(Func<INntpClient, Task<T>> action)
    {
        using var lease = Acquire();
        return await action(lease.Client).ConfigureAwait(false);
    }

    private async Task<T> UseStreamingAsync<T>(
        Func<INntpClient, Action<ArticleBodyResult>, Task<T>> action,
        Action<ArticleBodyResult>? onConnectionReadyAgain)
    {
        var lease = Acquire();
        var lifetime = new StreamingLifetime(lease, onConnectionReadyAgain);
        try
        {
            return await action(lease.Client, lifetime.ConnectionReady).ConfigureAwait(false);
        }
        catch
        {
            lifetime.ConnectionReady(ArticleBodyResult.NotRetrieved);
            throw;
        }
        finally
        {
            lifetime.CallCompleted();
        }
    }

    public override Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken) =>
        UseAsync(client => client.ConnectAsync(host, port, useSsl, cancellationToken));

    public override Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken) =>
        UseAsync(client => client.AuthenticateAsync(user, pass, cancellationToken));

    public override Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        UseAsync(client => client.StatAsync(segmentId, cancellationToken));

    public override Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        UseAsync(client => client.HeadAsync(segmentId, cancellationToken));

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        UseStreamingAsync(
            (client, onReady) => client.DecodedBodyAsync(segmentId, onReady, cancellationToken),
            onConnectionReadyAgain: null);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken) =>
        UseStreamingAsync(
            (client, onReady) => client.DecodedBodyAsync(segmentId, onReady, cancellationToken),
            onConnectionReadyAgain);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        UseStreamingAsync(
            (client, onReady) => client.DecodedArticleAsync(segmentId, onReady, cancellationToken),
            onConnectionReadyAgain: null);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken) =>
        UseStreamingAsync(
            (client, onReady) => client.DecodedArticleAsync(segmentId, onReady, cancellationToken),
            onConnectionReadyAgain);

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
        UseAsync(client => client.DateAsync(cancellationToken));

    public override void CloseIdleConnections(string? host = null)
    {
        using var lease = Acquire();
        lease.Client.CloseIdleConnections(host);
    }

    public override int PipeliningDepth
    {
        get
        {
            using var lease = Acquire();
            return lease.Client.PipeliningDepth;
        }
    }

    public override async Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken)
    {
        var lease = Acquire();
        try
        {
            var inner = await lease.Client.AcquireExclusiveConnectionAsync(segmentId, cancellationToken)
                .ConfigureAwait(false);
            var lifetime = new StreamingLifetime(lease, inner.OnConnectionReadyAgain);
            return new UsenetExclusiveConnection(
                lifetime.ConnectionReady,
                lease.Client,
                lifetime.CallCompleted);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken) =>
        exclusiveConnection.Owner is { } owner
            ? UseExclusiveAsync(
                exclusiveConnection,
                client => client.DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken),
                owner)
            : UseAsync(client => client.DecodedBodyAsync(segmentId, exclusiveConnection, cancellationToken));

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken) =>
        exclusiveConnection.Owner is { } owner
            ? UseExclusiveAsync(
                exclusiveConnection,
                client => client.DecodedArticleAsync(segmentId, exclusiveConnection, cancellationToken),
                owner)
            : UseAsync(client => client.DecodedArticleAsync(segmentId, exclusiveConnection, cancellationToken));

    private static async Task<T> UseExclusiveAsync<T>(
        UsenetExclusiveConnection exclusiveConnection,
        Func<INntpClient, Task<T>> action,
        INntpClient owner)
    {
        try
        {
            return await action(owner).ConfigureAwait(false);
        }
        catch
        {
            exclusiveConnection.OnConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }
        finally
        {
            exclusiveConnection.OnCallCompleted?.Invoke();
        }
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = Acquire();
        await foreach (var result in lease.Client.StatsPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = Acquire();
        await foreach (var result in lease.Client.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var lease = Acquire();
        await foreach (var result in lease.Client.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken)
                           .WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return result;
    }

    public override Task CheckAllSegmentsAsync(
        IEnumerable<string> segmentIds,
        int concurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken) =>
        UseAsync(client => client.CheckAllSegmentsAsync(segmentIds, concurrency, progress, cancellationToken));

    public override Task CheckAllSegmentsPipelinedAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken) =>
        UseAsync(client => client.CheckAllSegmentsPipelinedAsync(
            segmentIds, depth, fallbackConcurrency, progress, cancellationToken));

    public Task PrewarmQueueAsync(int targetConnections, CancellationToken cancellationToken) =>
        UseAsync(client => client is IQueueConnectionWarmer warmer
            ? warmer.PrewarmQueueAsync(targetConnections, cancellationToken)
            : Task.CompletedTask);

    public Task PrewarmHealthCheckAsync(CancellationToken cancellationToken) =>
        UseAsync(client => client is IQueueConnectionWarmer warmer
            ? warmer.PrewarmHealthCheckAsync(cancellationToken)
            : Task.CompletedTask);

    public override void Dispose()
    {
        INntpClient? dispose = null;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _current.Retired = true;
            if (_current.ActiveCalls == 0) dispose = _current.Client;
        }

        SafeDispose(dispose);
        GC.SuppressFinalize(this);
    }

    private static void SafeDispose(INntpClient? client)
    {
        if (client is null) return;
        try
        {
            client.Dispose();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to dispose a retired NNTP client graph.");
        }
    }

    private sealed class Generation(INntpClient client)
    {
        public INntpClient Client { get; } = client;
        public int ActiveCalls { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class Lease(DrainingNntpClient owner, Generation generation) : IDisposable
    {
        private DrainingNntpClient? _owner = owner;
        public INntpClient Client => generation.Client;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(generation);
        }
    }

    private sealed class StreamingLifetime(
        Lease lease,
        Action<ArticleBodyResult>? innerConnectionReady)
    {
        private int _connectionReadyStarted;
        private int _connectionReadyCompleted;
        private int _callCompleted;
        private int _released;

        public void ConnectionReady(ArticleBodyResult result)
        {
            if (Interlocked.Exchange(ref _connectionReadyStarted, 1) != 0) return;
            try
            {
                innerConnectionReady?.Invoke(result);
            }
            finally
            {
                Volatile.Write(ref _connectionReadyCompleted, 1);
                TryRelease();
            }
        }

        public void CallCompleted()
        {
            Interlocked.Exchange(ref _callCompleted, 1);
            TryRelease();
        }

        private void TryRelease()
        {
            if (Volatile.Read(ref _connectionReadyCompleted) == 0
                || Volatile.Read(ref _callCompleted) == 0) return;
            if (Interlocked.Exchange(ref _released, 1) == 0) lease.Dispose();
        }
    }
}
