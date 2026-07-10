using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

/// <summary>
/// This client is responsible for delegating NNTP commands to a connection pool.
///   * The connection pool enforces a maximum number of allowed connections
///   * When a connection is available, the NNTP command executes immediately
///   * When a connection is not available, the NNTP command waits until a connection becomes available.
///   * When multiple commands are awaiting a connection,
///     then BODY/ARTICLE commands have higher priority than STAT/HEAD/DATE commands.
/// </summary>
/// <param name="connectionPool"></param>
/// <param name="type"></param>
/// <param name="circuitBreaker"></param>
[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class MultiConnectionNntpClient(
    ConnectionPool<INntpClient> connectionPool,
    ProviderType type,
    ProviderCircuitBreaker circuitBreaker,
    string host,
    long? byteLimit,
    long bytesUsedOffset,
    int priority,
    bool prepOnly,
    bool prepSpreadEnabled,
    int? pipeliningDepth = null,
    int? healthPipeliningDepth = null
) : NntpClient
{
    public ProviderType ProviderType { get; } = type;
    public int Priority { get; } = priority;
    public string Host { get; } = host;
    public bool PrepOnly { get; } = prepOnly;
    public bool PrepSpreadEnabled { get; } = prepSpreadEnabled;

    public int? ConfiguredPipeliningDepth { get; } = pipeliningDepth;
    public int? ConfiguredHealthPipeliningDepth { get; } = healthPipeliningDepth;
    // null or non-positive = uncapped. Routing reads these to decide whether
    // this provider should be skipped when it has exhausted its block.
    public long? ByteLimit { get; } = byteLimit;
    public long BytesUsedOffset { get; } = bytesUsedOffset;
    public bool IsTripped => circuitBreaker.IsTripped || connectionPool.CapacityUnavailable;
    public int LiveConnections => connectionPool.LiveConnections;
    public int IdleConnections => connectionPool.IdleConnections;
    public int ActiveConnections => connectionPool.ActiveConnections;
    public int AvailableConnections => connectionPool.AvailableConnections;

    private int _pendingSelections;
    public int PendingSelections => Volatile.Read(ref _pendingSelections);
    public void ReservePending() => Interlocked.Increment(ref _pendingSelections);
    public void ReleasePending() => Interlocked.Decrement(ref _pendingSelections);

    public bool HasSpareConnection => AvailableConnections - PendingSelections > 0;

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "STAT",
            SemaphorePriority.Low,
            (connection, _) => connection.StatAsync(segmentId, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "HEAD",
            SemaphorePriority.Low,
            (connection, _) => connection.HeadAsync(segmentId, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(SegmentId segmentId, CancellationToken ct)
    {
        return RunWithConnection(
            "BODY",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedBodyAsync(segmentId, onDone, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedArticleAsync(segmentId, onDone, ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken ct)
    {
        return RunWithConnection(
            "DATE",
            SemaphorePriority.Low,
            (connection, _) => connection.DateAsync(ct),
            onConnectionReadyAgain: null,
            ct
        );
    }

    public override void CloseIdleConnections(string? targetHost = null)
    {
        if (targetHost == null || string.Equals(targetHost, Host, StringComparison.OrdinalIgnoreCase))
            connectionPool.CloseIdleConnections();
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "BODY",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedBodyAsync(segmentId, onDone, ct),
            onConnectionReadyAgain,
            ct
        );
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct
    )
    {
        return RunWithConnection(
            "ARTICLE",
            GetDownloadPriority(ct),
            (connection, onDone) => connection.DecodedArticleAsync(segmentId, onDone, ct),
            onConnectionReadyAgain,
            ct
        );
    }

    private async Task<T> RunWithConnection<T>
    (
        string name,
        SemaphorePriority priority,
        Func<INntpClient, Action<ArticleBodyResult>, Task<T>> command,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken ct,
        int retryCount = 1
    ) where T : UsenetResponse
    {
        if (!circuitBreaker.TryBeginAttempt(out var halfOpenProbe))
            throw new ProviderCircuitOpenException(Host);

        try
        {
            while (retryCount >= 0)
            {
                ConnectionLock<INntpClient>? connectionLock = null;
                var callbackLock = new object();
                var commandReturned = false;
                ArticleBodyResult? deferredConnectionResult = null;
                try
                {
                    connectionLock = await connectionPool.GetConnectionLockAsync(priority, ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e.IsCancellationException())
                {
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                    throw;
                }
                catch (Exception e)
                {
                    if (!e.TryGetCausingException(out UsenetConnectionLimitException _))
                        circuitBreaker.RecordFailure(halfOpenProbe);
                    LogException(() => connectionLock?.Replace());
                    LogException(() => connectionLock?.Dispose());
                    Log.Debug(e, "Error getting connection-lock. Failing over to another provider.");
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                    throw;
                }

                T? result;
                try
                {
                    result = await command(connectionLock.Connection, OnConnectionReadyAgain).ConfigureAwait(false);
                    ArticleBodyResult? deferred;
                    lock (callbackLock)
                    {
                        commandReturned = true;
                        deferred = deferredConnectionResult;
                        deferredConnectionResult = null;
                    }

                    if (deferred.HasValue)
                        HandleConnectionReadyAgain(deferred.Value);
                }
                catch (Exception e) when (e.IsCancellationException())
                {
                    LogException(() => connectionLock?.Replace());
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                    throw;
                }
                catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException _))
                {
                    // A completed 430/423 response leaves the NNTP command stream in
                    // sync. Reuse the socket and let MultiProvider fail over directly;
                    // retrying the same provider only extends prep tails and drains the
                    // warm pool when an article is genuinely absent.
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                    throw;
                }
                catch (Exception e) when (name is "BODY" or "ARTICLE" && e.TryGetCausingException(out TimeoutException _))
                {
                    // Read-timeout on BODY/ARTICLE means the provider stopped responding
                    // mid-command. A fresh socket to the same provider is unlikely to fare
                    // any better, and burning another timeout retrying here just doubles
                    // the wait before MultiProviderNntpClient can fall over to the next
                    // provider. Replace the socket (the read may have left partial bytes
                    // on the wire) and propagate so the outer provider loop moves on.
                    circuitBreaker.RecordFailure(halfOpenProbe);
                    LogException(() => connectionLock?.Replace());
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                    throw;
                }
                catch (Exception e)
                {
                    circuitBreaker.RecordFailure(halfOpenProbe);
                    LogException(() => connectionLock?.Replace());
                    LogException(() => connectionLock?.Dispose());
                    if (retryCount > 0)
                    {
                        Log.Debug(e, $"Error executing nntp {name} command. Retrying with a new connection.");
                        retryCount--;
                        continue;
                    }

                    Log.Warning(e, $"Error executing nntp {name} command.");
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                    throw;
                }

                circuitBreaker.RecordSuccess(halfOpenProbe);

                // stat, head, and date
                if (name is "STAT" or "HEAD" or "DATE")
                {
                    LogException(() => connectionLock?.Dispose());
                }
            
                // body and article
                else if ((result?.Success ?? false) == false)
                {
                    LogException(() => connectionLock?.Dispose());
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved));
                }

                return result!;

                void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
                {
                    lock (callbackLock)
                    {
                        // A synchronous NotRetrieved belongs to a command that is
                        // about to throw and may still fail over to another provider.
                        // A background NotRetrieved after a successful response means
                        // the body was interrupted and this socket must be replaced.
                        if (!commandReturned && articleBodyResult == ArticleBodyResult.NotRetrieved)
                        {
                            deferredConnectionResult = articleBodyResult;
                            return;
                        }
                    }

                    HandleConnectionReadyAgain(articleBodyResult);
                }

                void HandleConnectionReadyAgain(ArticleBodyResult articleBodyResult)
                {
                    if (articleBodyResult == ArticleBodyResult.NotRetrieved)
                        LogException(() => connectionLock?.Replace());

                    LogException(() => connectionLock?.Dispose());
                    // The command already returned a usable response, so release the
                    // outer download permit even when its background body was aborted.
                    LogException(() => onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved));
                }
            }

            Log.Error("Unreachable code reached");
            throw new InvalidOperationException("Unreachable code ");
        }
        finally
        {
            circuitBreaker.EndAttempt(halfOpenProbe);
        }
    }

    public override IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(c => c.StatsPipelinedAsync(segmentIds, depth, cancellationToken), cancellationToken);

    public override IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(c => c.DecodedBodiesPipelinedAsync(segmentIds, depth, cancellationToken), cancellationToken);

    public override IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth, CancellationToken cancellationToken)
        => RunPipelinedAsync(c => c.DecodedArticlesPipelinedAsync(segmentIds, depth, cancellationToken), cancellationToken);

    private async IAsyncEnumerable<T> RunPipelinedAsync<T>(
        Func<INntpClient, IAsyncEnumerable<T>> batchFactory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!circuitBreaker.TryBeginAttempt(out var halfOpenProbe))
            throw new ProviderCircuitOpenException(Host);

        var priority = GetDownloadPriority(cancellationToken);
        try
        {
            var connectionLock = await connectionPool.GetConnectionLockAsync(priority, cancellationToken).ConfigureAwait(false);
            var completed = false;
            await using var enumerator = batchFactory(connectionLock.Connection)
                .GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    T current;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            completed = true;
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (Exception e) when (!e.IsCancellationException())
                    {
                        circuitBreaker.RecordFailure(halfOpenProbe);
                        connectionLock.Replace();
                        throw;
                    }

                    circuitBreaker.RecordSuccess(halfOpenProbe);
                    yield return current;
                }
            }
            finally
            {
                if (!completed) connectionLock.Replace();
                connectionLock.Dispose();
            }
        }
        finally
        {
            circuitBreaker.EndAttempt(halfOpenProbe);
        }
    }

    private static SemaphorePriority GetDownloadPriority(CancellationToken ct)
    {
        return ct.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
    }

    private static void LogException(Action? action)
    {
        try
        {
            action?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Unhandled exception");
        }
    }

    public override void Dispose()
    {
        connectionPool.Dispose();
        GC.SuppressFinalize(this);
    }
}
