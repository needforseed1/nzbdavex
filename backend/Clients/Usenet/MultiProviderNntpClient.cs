using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    ProviderUsageTracker usageTracker,
    MetricsWriter? metricsWriter = null,
    ProviderBytesTracker? bytesTracker = null,
    Func<bool>? cascadeEnabled = null,
    int applicationConnectionLimit = UsenetStreamingClient.ApplicationConnectionLimit,
    Func<int>? warmValidationConcurrency = null,
    TimeSpan? providerAttemptTimeout = null,
    TimeSpan? providerOperationTimeout = null
) : NntpClient, IQueueConnectionWarmer
{
    private const int BulkStatProbeSize = 32;
    private const int BulkStatProbeThreshold = 256;
    private static readonly TimeSpan HealthConnectionPrimeTimeout = TimeSpan.FromSeconds(5);
    private const int HealthReclamationIdleFloor = 4;
    private const double PartialStatMinimumCoverage = 0.50;
    private static readonly TimeSpan BulkStatProbeJoinWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BulkStatProbeCapacitySettleWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BulkStatProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultProviderAttemptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultProviderOperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();
    private static readonly AsyncLocal<BulkStatPlan?> BulkStatPlanContext = new();

    /// <summary>
    /// Tag the current async flow with a read-session id so SegmentFetch rows
    /// emitted while fulfilling this read can be correlated back to the session.
    /// Disposing the returned scope restores the previous value.
    /// </summary>
    public static IDisposable BeginReadSessionScope(Guid readSessionId)
    {
        var previous = ReadSessionScope.Value;
        ReadSessionScope.Value = readSessionId;
        return new ScopeReleaser(() => ReadSessionScope.Value = previous);
    }

    private sealed class ScopeReleaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    // Per-call attribution. Caller (e.g. PlaybackFastVerifier) sets a mutable
    // holder on AttributionContext BEFORE invoking; we read it inside the call and
    // mutate Host on a non-"missing" response. AsyncLocal reliably flows the holder
    // reference DOWN to us; mutating its property is then visible to the caller via
    // their reference (which sidesteps AsyncLocal's child→parent non-propagation).
    public sealed class ResponderAttribution { public string? Host; }
    public static readonly AsyncLocal<ResponderAttribution?> AttributionContext = new();

    private readonly object _selectLock = new();
    private int _prepSpreadCursor;
    private int TotalLiveConnections => providers.Sum(provider => provider.LiveConnections);
    private int ApplicationConnectionLimit => applicationConnectionLimit;
    private int WarmValidationConcurrency => Math.Clamp(warmValidationConcurrency?.Invoke() ?? 32, 1, 256);
    private TimeSpan ProviderAttemptTimeout => providerAttemptTimeout ?? DefaultProviderAttemptTimeout;
    private TimeSpan ProviderOperationTimeout => providerOperationTimeout ?? DefaultProviderOperationTimeout;

    public async Task PrewarmQueueAsync(int targetConnections, CancellationToken cancellationToken)
    {
        var eligible = providers
            .Where(x => x.ProviderType == ProviderType.Pooled)
            .Where(x => !x.IsTripped)
            .Where(x => !IsOverLimit(x))
            .ToList();
        var totalCapacity = eligible.Sum(x => x.MaxConnections);
        var target = Math.Clamp(targetConnections, 0, totalCapacity);
        if (target == 0) return;

        var allocations = eligible.ToDictionary(x => x, _ => 0);
        for (var i = 0; i < target; i++)
        {
            var provider = eligible
                .Where(x => allocations[x] < x.MaxConnections)
                .OrderBy(x => allocations[x] / (double)x.MaxConnections)
                .ThenBy(x => x.Priority)
                .First();
            allocations[provider]++;
        }

        await Task.WhenAll(allocations
            .Where(x => x.Value > 0)
            .Select(async allocation =>
            {
                try
                {
                    await allocation.Key.PrewarmAsync(allocation.Value, cancellationToken).ConfigureAwait(false);
                    Log.Debug(
                        "Queue prewarm provider={Provider} target={Target} live={Live} idle={Idle}",
                        allocation.Key.Host, allocation.Value,
                        allocation.Key.LiveConnections, allocation.Key.IdleConnections);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Log.Debug(e, "Queue prewarm failed for provider={Provider} target={Target}",
                        allocation.Key.Host, allocation.Value);
                }
            })).ConfigureAwait(false);
    }

    public async Task PrewarmHealthCheckAsync(CancellationToken cancellationToken)
    {
        var validationConcurrency = WarmValidationConcurrency;
        // Pool providers are already warmed by queue prewarm and then exercised by
        // BODY/ARTICLE prep. Health-only and backup+STAT providers otherwise sit
        // outside that path, so establish the same bounded warm floor used at
        // startup while prep is busy. The pool can still grow to its configured
        // maximum when health work actually demands it.
        var eligible = providers
            .Where(x => x.ProviderType == ProviderType.BackupAndStats ||
                        (UsenetStreamingClient.HealthProviderPrewarmEnabled &&
                         x.ProviderType == ProviderType.HealthChecksOnly))
            .Where(x => !x.IsTripped)
            .Where(x => !IsOverLimit(x))
            .ToList();

        await Task.WhenAll(eligible.Select(async provider =>
        {
            var target = UsenetStreamingClient.GetWarmConnectionTarget(
                provider.ProviderType, provider.MaxConnections);
            try
            {
                await provider.PrewarmAsync(target, cancellationToken).ConfigureAwait(false);
                var refreshCount = Math.Min(target, provider.IdleConnections);
                await provider.RefreshWarmConnectionsAsync(
                        refreshCount, validationConcurrency, cancellationToken)
                    .ConfigureAwait(false);
                Log.Debug(
                    "Health prewarm provider={Provider} target={Target} refreshed={Refreshed} " +
                    "validationConcurrency={ValidationConcurrency} live={Live} idle={Idle}",
                    provider.Host, target, refreshCount, validationConcurrency,
                    provider.LiveConnections, provider.IdleConnections);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Debug(e, "Health prewarm failed for provider={Provider} target={Target}",
                    provider.Host, target);
            }
        })).ConfigureAwait(false);
    }

    public async Task PrewarmPrimaryHealthCheckAsync(CancellationToken cancellationToken)
    {
        var validationConcurrency = WarmValidationConcurrency;
        // First-segment prep intentionally reads only the metadata prefix of each
        // article. Those partially consumed BODY connections are discarded to keep
        // the NNTP command stream synchronized, so a Primary pool can be cold at the
        // prep-to-STAT boundary even when queue prewarm filled it beforehand.
        // Refill opportunistically after that boundary. The pool's speculative
        // creation path never queues ahead of foreground playback or health work.
        var eligible = providers
            .Where(x => x.ProviderType == ProviderType.Pooled)
            .Where(x => !x.IsTripped)
            .Where(x => !IsOverLimit(x))
            .ToList();

        await Task.WhenAll(eligible.Select(async provider =>
        {
            var target = UsenetStreamingClient.GetHealthCheckWarmConnectionTarget(
                provider.ProviderType, provider.MaxConnections);
            try
            {
                await provider.PrewarmAsync(target, cancellationToken).ConfigureAwait(false);
                var refreshCount = Math.Min(target, provider.IdleConnections);
                await provider.RefreshWarmConnectionsAsync(
                        refreshCount, validationConcurrency, cancellationToken)
                    .ConfigureAwait(false);
                Log.Debug(
                    "Primary health prewarm provider={Provider} target={Target} refreshed={Refreshed} " +
                    "validationConcurrency={ValidationConcurrency} live={Live} idle={Idle}",
                    provider.Host, target, refreshCount, validationConcurrency,
                    provider.LiveConnections, provider.IdleConnections);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Debug(e, "Primary health prewarm failed for provider={Provider} target={Target}",
                    provider.Host, target);
            }
        })).ConfigureAwait(false);
    }

    public Task PrimeHealthCheckAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        CancellationToken cancellationToken) =>
        PrimeHealthProvidersAsync(
            providers.Where(provider => provider.ProviderType is
                ProviderType.HealthChecksOnly or ProviderType.BackupAndStats),
            segmentIds,
            depth,
            "dedicated",
            cancellationToken);

    public Task PrimePrimaryHealthCheckAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        CancellationToken cancellationToken) =>
        PrimeHealthProvidersAsync(
            providers.Where(provider => provider.ProviderType == ProviderType.Pooled),
            segmentIds,
            depth,
            "primary",
            cancellationToken);

    private async Task PrimeHealthProvidersAsync(
        IEnumerable<MultiConnectionNntpClient> candidates,
        IReadOnlyList<string> segmentIds,
        int depth,
        string phase,
        CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) return;
        var concurrency = WarmValidationConcurrency;
        var effectiveDepth = Math.Clamp(depth, 1, segmentIds.Count);
        var eligible = candidates
            .Where(provider => !provider.IsTripped)
            .Where(provider => !IsOverLimit(provider))
            .ToArray();

        await Task.WhenAll(eligible.Select(async provider =>
        {
            // Keep priming to one bounded wave. Auto touches up to 32 sockets;
            // an explicit higher setting lets advanced users exercise a larger
            // already-connected pool without adding sockets or health lanes.
            var connectionCount = Math.Min(provider.IdleConnections, concurrency);
            if (connectionCount == 0) return;
            var timer = Stopwatch.StartNew();
            using var primeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            primeCts.CancelAfter(HealthConnectionPrimeTimeout);
            try
            {
                await provider.PrimeHealthConnectionsAsync(
                        segmentIds, effectiveDepth, connectionCount, concurrency, primeCts.Token)
                    .ConfigureAwait(false);
                Log.Debug(
                    "Health STAT prime phase={Phase} provider={Provider} connections={Connections} " +
                    "depth={Depth} commands={Commands} ms={ElapsedMs}",
                    phase, provider.Host, connectionCount, effectiveDepth,
                    connectionCount * segmentIds.Count, timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && primeCts.IsCancellationRequested)
            {
                Log.Debug(
                    "Health STAT prime timed out phase={Phase} provider={Provider} " +
                    "connections={Connections} depth={Depth} ms={ElapsedMs}",
                    phase, provider.Host, connectionCount, effectiveDepth, timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Debug(e,
                    "Health STAT prime failed phase={Phase} provider={Provider} " +
                    "connections={Connections} depth={Depth} ms={ElapsedMs}",
                    phase, provider.Host, connectionCount, effectiveDepth, timer.ElapsedMilliseconds);
            }
        })).ConfigureAwait(false);
    }

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            (x, attemptToken) => x.StatAsync(segmentId, attemptToken),
            cancellationToken,
            statOperation: true);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            (x, attemptToken) => x.HeadAsync(segmentId, attemptToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            (x, attemptToken) => x.DecodedBodyAsync(segmentId, attemptToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(
            (x, attemptToken) => x.DecodedArticleAsync(segmentId, attemptToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(
            (x, attemptToken) => x.DateAsync(attemptToken), cancellationToken);
    }

    public override void CloseIdleConnections(string? host = null)
    {
        foreach (var provider in providers)
            provider.CloseIdleConnections(host);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                (x, attemptToken) => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, attemptToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken
    )
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                (x, attemptToken) => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, attemptToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>
    (
        Func<INntpClient, CancellationToken, Task<T>> task,
        CancellationToken cancellationToken,
        bool statOperation = false
    ) where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var orderedProviders = statOperation
            ? SelectOrderedProvidersForStat(cancellationToken, out var reserved)
            : SelectOrderedProviders(cancellationToken, out reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var operationStopwatch = Stopwatch.StartNew();
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingOperationTime = ProviderOperationTimeout - operationStopwatch.Elapsed;
            if (remainingOperationTime <= TimeSpan.Zero) break;
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            var stopwatch = Stopwatch.StartNew();
            var maximumAttemptTime = isLastProvider
                ? ProviderOperationTimeout
                : ProviderAttemptTimeout;
            var attemptTimeout = maximumAttemptTime < remainingOperationTime
                ? maximumAttemptTime
                : remainingOperationTime;
            var attemptCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var attemptLifetimeTransferred = false;
            attemptCts.CancelAfter(attemptTimeout);
            try
            {
                var result = await task.Invoke(provider, attemptCts.Token).ConfigureAwait(false);
                stopwatch.Stop();

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    RecordFetch(provider.Id, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                    (priorMisses ??= new()).Add((provider.Id, SegmentFetch.FetchStatus.Missing));
                    continue;
                }

                // attribute the response to this provider, unless it was a "missing" hit
                // from the last provider (in which case nobody actually answered).
                if (attribution != null && result.ResponseType != UsenetResponseType.NoArticleWithThatMessageId)
                    attribution.Host = provider.Host;

                // record per-queue-item attribution only for bytes-bearing responses (BODY/ARTICLE).
                if (result is UsenetDecodedBodyResponse or UsenetDecodedArticleResponse
                    && result.ResponseType is UsenetResponseType.ArticleRetrievedBodyFollows
                                          or UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
                {
                    usageTracker.RecordSuccess(provider.Id);
                    RecordFetch(provider.Id, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, i);
                    if (i > 0)
                    {
                        usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, rescuer: provider.Id);
                    }
                    result = WrapStreamForByteCounting(result, provider.Id);
                }
                else
                {
                    RecordFetch(provider.Id, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                }

                result = PreserveCallerCancellationForStreamingResult(
                    result, attemptCts, out attemptLifetimeTransferred);
                return result;
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                var providerTimeout = new TimeoutException(
                    $"Provider {provider.Host} did not become usable within " +
                    $"{attemptTimeout.TotalSeconds:0.#} seconds.", e);
                RecordFetch(provider.Id, SegmentFetch.FetchStatus.Timeout, stopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= new()).Add((provider.Id, SegmentFetch.FetchStatus.Timeout));
                lastException = ExceptionDispatchInfo.Capture(providerTimeout);
                Log.Debug(providerTimeout,
                    "Timed out waiting for NNTP provider {Provider}; trying another provider.",
                    provider.Host);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.Id, reason, stopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= new()).Add((provider.Id, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                if (!attemptLifetimeTransferred) attemptCts.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (lastException?.SourceException is TimeoutException timeoutException)
            throw new CouldNotConnectToUsenetException(
                "No Usenet provider could complete the operation.",
                timeoutException);
        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private void RecordFetch(string host, SegmentFetch.FetchStatus status, long durationMs, int retries)
    {
        if (metricsWriter == null) return;
        metricsWriter.RecordFetch(new SegmentFetch
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Provider = host,
            ReadSessionId = ReadSessionScope.Value,
            Bytes = 0, // bytes flow lazily through CountingYencStream → ProviderBytesTracker
            DurationMs = (int)Math.Min(int.MaxValue, durationMs),
            Status = status,
            Retries = retries,
        });
    }

    private void RecordFailoverMisses(
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses,
        string rescuer)
    {
        if (metricsWriter == null || priorMisses == null) return;
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var (from, reason) in priorMisses)
        {
            metricsWriter.RecordFailoverMiss(new FailoverMiss
            {
                At = at,
                FromProvider = from,
                ToProvider = rescuer,
                Reason = reason,
            });
        }
    }

    private T WrapStreamForByteCounting<T>(T result, string host) where T : UsenetResponse
    {
        if (bytesTracker == null) return result;
        return result switch
        {
            UsenetDecodedBodyResponse b
                => (T)(object)(b with
                {
                    Stream = new CountingYencStream(
                        b.Stream, bytesTracker, host, bytes => usageTracker.RecordBytes(host, bytes))
                }),
            UsenetDecodedArticleResponse a
                => (T)(object)(a with
                {
                    Stream = new CountingYencStream(
                        a.Stream, bytesTracker, host, bytes => usageTracker.RecordBytes(host, bytes))
                }),
            _ => result,
        };
    }

    private static T PreserveCallerCancellationForStreamingResult<T>(
        T result,
        ContextualCancellationTokenSource attemptCts,
        out bool lifetimeTransferred) where T : UsenetResponse
    {
        lifetimeTransferred = false;
        switch (result)
        {
            case UsenetDecodedBodyResponse body when body.Stream is not null:
                attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
                lifetimeTransferred = true;
                return (T)(object)(body with
                {
                    Stream = new LifetimeYencStream(body.Stream, attemptCts),
                });
            case UsenetDecodedArticleResponse article when article.Stream is not null:
                attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
                lifetimeTransferred = true;
                return (T)(object)(article with
                {
                    Stream = new LifetimeYencStream(article.Stream, attemptCts),
                });
            default:
                return result;
        }
    }

    private static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        if (ex is TimeoutException) return SegmentFetch.FetchStatus.Timeout;
        if (ex is UnauthorizedAccessException) return SegmentFetch.FetchStatus.Auth;
        if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException) return SegmentFetch.FetchStatus.Network;
        return SegmentFetch.FetchStatus.Other;
    }

    private List<MultiConnectionNntpClient> SelectOrderedProviders(
        CancellationToken cancellationToken,
        out MultiConnectionNntpClient? reserved,
        IReadOnlySet<MultiConnectionNntpClient>? excluded = null)
    {
        lock (_selectLock)
        {
            var priority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
            var enabled = providers
                .Where(x => x.ProviderType is not (ProviderType.Disabled or ProviderType.HealthChecksOnly))
                .Where(x => priority != SemaphorePriority.High || !x.PrepOnly)
                .Where(x => !IsOverLimit(x))
                .Where(x => excluded?.Contains(x) != true)
                .ToList();

            var healthy = enabled.Where(x => !x.IsTripped).ToList();
            var pool = healthy.Count > 0 ? healthy : enabled;

            if (priority != SemaphorePriority.High)
            {
                var spreadPool = pool
                    .Where(x => x.ProviderType == ProviderType.Pooled && x.PrepSpreadEnabled)
                    .ToList();
                if (spreadPool.Count > 0)
                {
                    // Do not pin prep requests to a provider that has not established
                    // a single usable socket while another pooled provider is ready.
                    // Prewarm runs independently and the provider joins this set as
                    // soon as its first connection is published.
                    var readySpreadPool = spreadPool.Where(x => x.LiveConnections > 0).ToList();
                    if (readySpreadPool.Count > 0)
                    {
                        var spreadOrdered = OrderPrepSpreadProviders(readySpreadPool, pool);
                        reserved = spreadOrdered[0];
                        reserved.ReservePending();
                        return spreadOrdered;
                    }
                }
            }

            // Prefer capacity that exists now over waiting on a nominally
            // higher-tier provider with no viable sockets. Provider role still
            // orders equally-ready candidates, so backups remain rescue-only
            // while a pooled provider can serve the operation.
            var byTier = pool
                .OrderBy(ReadinessRank)
                .ThenBy(x => x.ProviderType);
            var prioritized = cascadeEnabled?.Invoke() == true
                ? byTier.ThenBy(EffectivePriority)
                : byTier;
            var ordered = prioritized
                .ThenByDescending(x => GetRemainingBytes(x))
                .ThenBy(EstimatedDeliveryScore)
                .ToList();

            reserved = ordered.Count > 0 ? ordered[0] : null;
            reserved?.ReservePending();
            return ordered;
        }
    }

    private List<MultiConnectionNntpClient> OrderPrepSpreadProviders(
        IReadOnlyList<MultiConnectionNntpClient> spreadPool,
        IReadOnlyList<MultiConnectionNntpClient> pool)
    {
        var rotation = _prepSpreadCursor++ % spreadPool.Count;
        var spread = spreadPool
            .Select((provider, index) => new
            {
                Provider = provider,
                RotationRank = (index - rotation + spreadPool.Count) % spreadPool.Count,
            })
            .OrderBy(x => PrepSpreadScore(x.Provider))
            .ThenBy(x => x.RotationRank)
            .ThenByDescending(x => GetRemainingBytes(x.Provider))
            .ThenBy(x => EffectivePriority(x.Provider))
            .Select(x => x.Provider)
            .ToList();
        var spreadSet = spread.ToHashSet();
        var fallback = pool
            .Where(x => !spreadSet.Contains(x))
            .OrderBy(ReadinessRank)
            .ThenBy(x => x.ProviderType)
            .ThenBy(EffectivePriority)
            .ThenByDescending(GetRemainingBytes)
            .ThenBy(EstimatedDeliveryScore);
        return spread.Concat(fallback).ToList();
    }

    private List<MultiConnectionNntpClient> SelectOrderedProvidersForStat(
        CancellationToken cancellationToken,
        out MultiConnectionNntpClient? reserved)
    {
        lock (_selectLock)
        {
            var priority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
            var enabled = providers
                .Where(x => x.ProviderType != ProviderType.Disabled)
                .Where(x => priority != SemaphorePriority.High || !x.PrepOnly ||
                            x.ProviderType == ProviderType.HealthChecksOnly)
                .Where(x => !IsOverLimit(x))
                .ToList();
            var healthy = enabled.Where(x => !x.IsTripped).ToList();
            var pool = healthy.Count > 0 ? healthy : enabled;

            var plan = BulkStatPlanContext.Value;
            if (plan is not null && ReferenceEquals(plan.Owner, this))
            {
                var preferred = pool
                    .Where(IsPrimaryStatProvider)
                    .Where(plan.IsPreferred)
                    .OrderBy(plan.SelectionScore)
                    .ThenBy(plan.ProbeRank)
                    .ThenBy(EffectivePriority)
                    .ToList();
                if (preferred.Count > 0)
                {
                    var preferredSet = preferred.ToHashSet();
                    var selectedPrimary = preferred[0];
                    // Partial providers are valuable primary workers, but when
                    // one reports a miss, go straight to the highest-coverage
                    // peer. Do not repeat the same likely-backbone miss through
                    // several other partial accounts before reaching a complete
                    // provider.
                    var preferredFallback = preferred
                        .Skip(1)
                        .OrderByDescending(plan.Coverage)
                        .ThenBy(plan.ProbeRank)
                        .ThenBy(EffectivePriority);
                    var plannedFallback = pool
                        .Where(x => !preferredSet.Contains(x))
                        .OrderBy(plan.ProbeRank)
                        .ThenBy(x => x.ProviderType)
                        .ThenBy(EffectivePriority)
                        .ThenByDescending(GetRemainingBytes);
                    var planned = new[] { selectedPrimary }
                        .Concat(preferredFallback)
                        .Concat(plannedFallback)
                        .ToList();
                    reserved = planned[0];
                    reserved.ReservePending();
                    return planned;
                }
            }

            // STAT transfers no article bodies, so spread health-eligible traffic by
            // normalized occupancy. Pending reservations prevent a burst of lanes
            // from all selecting the same nominally-fast provider before its sockets
            // have finished connecting. BackupOnly providers remain rescue-only.
            var primary = SelectPrimaryStatTier(pool)
                .OrderBy(StatSpreadScore)
                .ThenBy(EffectivePriority)
                .ThenBy(EstimatedDeliveryScore)
                .ThenByDescending(GetRemainingBytes)
                .ToList();
            var primarySet = primary.ToHashSet();
            var fallback = pool
                .Where(x => !primarySet.Contains(x))
                .OrderBy(x => x.ProviderType)
                .ThenBy(EffectivePriority)
                .ThenBy(EstimatedDeliveryScore)
                .ThenByDescending(GetRemainingBytes);
            var ordered = primary.Concat(fallback).ToList();

            reserved = ordered.Count > 0 ? ordered[0] : null;
            reserved?.ReservePending();
            return ordered;
        }
    }

    private static int EffectivePriority(MultiConnectionNntpClient provider)
    {
        const int saturationDemotion = 1 << 20;
        return provider.Priority + (provider.HasSpareConnection ? 0 : saturationDemotion);
    }

    private static int ReadinessRank(MultiConnectionNntpClient provider) =>
        provider.WarmConnections > 0 ? 0 : provider.LiveConnections > 0 ? 1 : 2;

    private double EstimatedDeliveryScore(MultiConnectionNntpClient provider)
    {
        var inFlight = provider.ActiveConnections + provider.PendingSelections + 1;
        var bytesPerMs = bytesTracker?.GetBytesPerMs(provider.Id) ?? 0d;
        return bytesPerMs > 0 ? inFlight / bytesPerMs : inFlight;
    }

    private static double PrepSpreadScore(MultiConnectionNntpClient provider)
    {
        // Route against capacity that exists now, not the configured maximum.
        // Otherwise a provider with slow or stalled handshakes can accumulate a
        // full share of reservations while healthy providers sit idle.
        var capacity = Math.Max(1, provider.LiveConnections);
        var committed = provider.ActiveConnections + provider.PendingSelections;
        return committed / (double)capacity;
    }

    private static double StatSpreadScore(MultiConnectionNntpClient provider)
    {
        var capacity = Math.Max(1, provider.ActiveConnections + provider.AvailableConnections);
        var committed = provider.ActiveConnections + provider.PendingSelections;
        return committed / (double)capacity;
    }

    private bool IsPrimaryStatProvider(MultiConnectionNntpClient provider) =>
        provider.ProviderType is (ProviderType.Pooled
            or ProviderType.BackupAndStats
            or ProviderType.HealthChecksOnly);

    internal static bool IsPartialStatProviderEligible(int found, int received)
    {
        if (found <= 0 || received <= 0 || found >= received)
            return false;

        var coverage = found / (double)received;
        return coverage >= PartialStatMinimumCoverage;
    }

    private HashSet<MultiConnectionNntpClient> SelectPreferredStatProviders(
        IEnumerable<BulkStatProbe> probes)
    {
        var successful = probes
            .Where(x => x.Success && x.Found > 0 && IsPrimaryStatProvider(x.Provider))
            .ToList();
        if (successful.Count == 0) return [];

        var bestCoverage = successful.Max(x => x.Found);

        return successful
            .Where(x => x.Found == bestCoverage ||
                        IsPartialStatProviderEligible(x.Found, x.Received))
            .Select(x => x.Provider)
            .ToHashSet();
    }

    private IEnumerable<MultiConnectionNntpClient> SelectPrimaryStatTier(
        IReadOnlyCollection<MultiConnectionNntpClient> pool)
    {
        // STAT traffic is tiny, so use every health-eligible provider. The bulk
        // plan weights lanes by measured rate and open capacity; dedicated
        // health accounts remain protected from BODY/ARTICLE traffic elsewhere.
        return pool.Where(IsPrimaryStatProvider);
    }

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.Id) + client.BytesUsedOffset;
        // Stop at the effective cutoff (95% of cap) so in-flight fetches that
        // already passed this check can't push the actual count past the cap.
        // See ProviderUsageHelper.EffectiveLimitFraction for the rationale.
        var effective = (long)(limit.Value * ProviderUsageHelper.EffectiveLimitFraction);
        return used >= effective;
    }

    private long GetRemainingBytes(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return long.MaxValue;
        var used = bytesTracker.GetLifetime(client.Id) + client.BytesUsedOffset;
        return Math.Max(0, limit.Value - used);
    }

    private static int ResolveBodyDepth(MultiConnectionNntpClient primary, int fallbackDepth)
    {
        return primary.ConfiguredPipeliningDepth is int d and > 0
            ? Math.Clamp(d, 1, 64)
            : fallbackDepth;
    }

    private static int ResolveHealthDepth(MultiConnectionNntpClient primary, int fallbackDepth)
    {
        return primary.ConfiguredHealthPipeliningDepth is int d and > 0
            ? Math.Clamp(d, 1, 64)
            : fallbackDepth;
    }

    private MultiConnectionNntpClient SelectReplacementPipelinedProvider(
        MultiConnectionNntpClient failedProvider,
        HashSet<MultiConnectionNntpClient> failedProviders,
        CancellationToken cancellationToken,
        ref MultiConnectionNntpClient? reserved)
    {
        failedProviders.Add(failedProvider);
        var replacements = SelectOrderedProviders(cancellationToken, out var replacementReservation, failedProviders);
        if (replacements.Count == 0) return failedProvider;

        reserved?.ReleasePending();
        reserved = replacementReservation;
        var replacement = replacements[0];
        Log.Debug(
            "Rotating pipelined BODY/ARTICLE traffic from failed provider {FailedProvider} to {ReplacementProvider}.",
            failedProvider.Host,
            replacement.Host);
        return replacement;
    }

    public override async Task CheckAllSegmentsPipelinedAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        usageTracker.BeginHealthCheck(segmentIds.Count);
        try
        {
            await CheckAllSegmentsPipelinedCoreAsync(
                segmentIds, depth, fallbackConcurrency, progress, cancellationToken).ConfigureAwait(false);
            usageTracker.CompleteHealthCheck(segmentIds.Count, 0);
        }
        catch (UsenetArticleNotFoundException)
        {
            // The health checker stops at the first globally unresolved article,
            // so the exact found total is unknown on failure but at least one
            // unique article is confirmed missing.
            usageTracker.CompleteHealthCheck(null, 1);
            throw;
        }
    }

    private async Task CheckAllSegmentsPipelinedCoreAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        int fallbackConcurrency,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        if (segmentIds.Count < BulkStatProbeThreshold || fallbackConcurrency <= 1)
        {
            await base.CheckAllSegmentsPipelinedAsync(
                segmentIds, depth, fallbackConcurrency, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var qualification = await QualifyBulkStatProvidersAsync(
            segmentIds, depth, fallbackConcurrency, cancellationToken).ConfigureAwait(false);
        if (qualification.Plan is null)
        {
            await base.CheckAllSegmentsPipelinedAsync(
                segmentIds, depth, fallbackConcurrency, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var remaining = segmentIds
            .Where((_, index) => !qualification.VerifiedIndexes.Contains(index))
            .ToArray();
        var verifiedCount = segmentIds.Count - remaining.Length;
        if (verifiedCount > 0) progress?.Report(verifiedCount);

        var previousPlan = BulkStatPlanContext.Value;
        BulkStatPlanContext.Value = qualification.Plan;
        using var connectionAllocation = qualification.Plan.BeginConnectionAllocation(fallbackConcurrency);
        try
        {
            if (remaining.Length == 0) return;
            var remainingProgress = progress is null ? null : new OffsetProgress(progress, verifiedCount);
            await base.CheckAllSegmentsPipelinedAsync(
                remaining, depth, fallbackConcurrency, remainingProgress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            qualification.Plan.LogSummary();
            BulkStatPlanContext.Value = previousPlan;
        }
    }

    private async Task<BulkStatQualification> QualifyBulkStatProvidersAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        int connectionDemand,
        CancellationToken cancellationToken)
    {
        List<MultiConnectionNntpClient> candidates;
        lock (_selectLock)
        {
            var priority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
            var enabled = providers
                .Where(x => x.ProviderType != ProviderType.Disabled)
                .Where(x => priority != SemaphorePriority.High || !x.PrepOnly ||
                            x.ProviderType == ProviderType.HealthChecksOnly)
                .Where(x => !IsOverLimit(x))
                .ToList();
            // Probe every eligible provider. A provider tripped by BODY prep is
            // allowed one controlled STAT recovery attempt below; excluding it
            // here would unnecessarily carry BODY state into the health phase.
            candidates = enabled;
        }

        var probeCandidates = SelectPrimaryStatTier(candidates).ToList();
        if (probeCandidates.Count == 0 ||
            (probeCandidates.Count == 1 && !probeCandidates[0].IsTripped))
            return BulkStatQualification.None;

        var sampleIndexes = SelectProbeIndexes(segmentIds.Count, BulkStatProbeSize);
        var sample = sampleIndexes.Select(index => segmentIds[index]).ToArray();
        var qualificationTimer = Stopwatch.StartNew();
        var pendingProbes = probeCandidates
            .Select(provider => ProbeStatProviderAsync(
                provider, sample, depth, provider.IsTripped, cancellationToken))
            .ToList();
        var probes = new List<BulkStatProbe>(pendingProbes.Count);
        var endedEarly = false;
        var capacityTarget = Math.Min(
            Math.Max(1, connectionDemand),
            ApplicationConnectionLimit);

        while (pendingProbes.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingProbes).ConfigureAwait(false);
            pendingProbes.Remove(completedTask);
            var completedProbe = await completedTask.ConfigureAwait(false);
            probes.Add(completedProbe);

            var hasCompletePrimary = completedProbe.Success &&
                                     completedProbe.Found == sample.Length;
            if (!hasCompletePrimary) continue;

            // A full-coverage result cannot be beaten. Give similarly fast peers a
            // brief chance to join the plan. If those completed providers cannot
            // supply the requested lane capacity, keep accepting completed probes
            // for a short bounded settle window. This prevents hundreds of lanes
            // from queueing behind the first small provider set while full-coverage
            // peers finish probes a few hundred milliseconds later.
            var settleTimer = Stopwatch.StartNew();
            if (pendingProbes.Count > 0)
                await Task.Delay(BulkStatProbeJoinWindow, cancellationToken).ConfigureAwait(false);

            await CollectCompletedProbesAsync().ConfigureAwait(false);
            while (pendingProbes.Count > 0 &&
                   GetPreferredCapacity() < capacityTarget &&
                   settleTimer.Elapsed < BulkStatProbeCapacitySettleWindow)
            {
                var remaining = BulkStatProbeCapacitySettleWindow - settleTimer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    break;
                var timeoutTask = Task.Delay(remaining, cancellationToken);
                var completed = await Task.WhenAny(
                    pendingProbes.Cast<Task>().Append(timeoutTask)).ConfigureAwait(false);
                if (ReferenceEquals(completed, timeoutTask))
                {
                    await timeoutTask.ConfigureAwait(false);
                    break;
                }

                var joinedProbe = (Task<BulkStatProbe>)completed;
                pendingProbes.Remove(joinedProbe);
                probes.Add(await joinedProbe.ConfigureAwait(false));
                await CollectCompletedProbesAsync().ConfigureAwait(false);
            }

            endedEarly = pendingProbes.Count > 0;
            break;

            async Task CollectCompletedProbesAsync()
            {
                var joinedProbes = pendingProbes.Where(x => x.IsCompleted).ToList();
                foreach (var joinedTask in joinedProbes)
                {
                    pendingProbes.Remove(joinedTask);
                    probes.Add(await joinedTask.ConfigureAwait(false));
                }
            }

            int GetPreferredCapacity() => SelectPreferredStatProviders(
                    probes.Where(x => x.Success))
                .Sum(provider => provider.MaxConnections);
        }

        foreach (var probe in probes)
            LogBulkStatProbe(probe, sample.Length);

        var successful = probes.Where(x => x.Success).ToList();
        if (successful.Count == 0) return BulkStatQualification.None;

        var bestCoverage = successful.Count == 0 ? 0 : successful.Max(x => x.Found);
        var preferred = SelectPreferredStatProviders(successful);
        var plan = new BulkStatPlan(this, probes, preferred);
        plan.ObserveLateProbes(pendingProbes, sample.Length, cancellationToken);

        Log.Information(
            "health-stat plan sample={Sample} preferred={Preferred} bestCoverage={BestCoverage}/{Sample} " +
            "preferredCapacity={PreferredCapacity}/{CapacityTarget} " +
            "qualificationMs={QualificationMs} endedEarly={EndedEarly}",
            sample.Length,
            preferred.Count == 0 ? "default-routing" : string.Join(',', preferred.Select(x => x.Host)),
            bestCoverage, sample.Length,
            preferred.Sum(provider => provider.MaxConnections), capacityTarget,
            qualificationTimer.ElapsedMilliseconds, endedEarly);

        var verified = new HashSet<int>();
        for (var sampleIndex = 0; sampleIndex < sampleIndexes.Count; sampleIndex++)
        {
            if (successful.Any(probe => probe.Exists[sampleIndex]))
                verified.Add(sampleIndexes[sampleIndex]);
        }

        return new BulkStatQualification(plan, verified);
    }

    private static async Task<BulkStatProbe> ProbeStatProviderAsync(
        MultiConnectionNntpClient provider,
        IReadOnlyList<string> segmentIds,
        int fallbackDepth,
        bool recoveryProbe,
        CancellationToken cancellationToken)
    {
        var exists = new bool[segmentIds.Count];
        var received = 0;
        var found = 0;
        var stopwatch = Stopwatch.StartNew();
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(BulkStatProbeTimeout);
        try
        {
            var providerDepth = ResolveHealthDepth(provider, fallbackDepth);
            var results = recoveryProbe
                ? provider.StatsPipelinedRecoveryProbeAsync(segmentIds, providerDepth, probeCts.Token)
                : provider.StatsPipelinedAsync(segmentIds, providerDepth, probeCts.Token);
            await foreach (var result in results
                               .WithCancellation(probeCts.Token).ConfigureAwait(false))
            {
                if (received >= segmentIds.Count ||
                    !string.Equals(result.SegmentId, segmentIds[received], StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Provider {provider.Host} returned an invalid pipelined STAT probe response.");

                exists[received] = result.Exists;
                if (result.Exists) found++;
                received++;
            }

            if (received != segmentIds.Count)
                throw new IOException(
                    $"Provider {provider.Host} ended a STAT probe after {received} of {segmentIds.Count} responses.");
            return new BulkStatProbe(provider, exists, received, found,
                segmentIds.Count - found, stopwatch.ElapsedMilliseconds, true, "ok");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BulkStatProbe(provider, exists, received, found,
                received - found, stopwatch.ElapsedMilliseconds, false, "timeout");
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug(e, "Bulk STAT provider probe failed for {Provider}.", provider.Host);
            return new BulkStatProbe(provider, exists, received, found,
                received - found, stopwatch.ElapsedMilliseconds, false, "failed");
        }
    }

    private static void LogBulkStatProbe(BulkStatProbe probe, int sampleSize)
    {
        var rate = probe.ElapsedMs > 0
            ? (int)Math.Round(probe.Received * 1000d / probe.ElapsedMs)
            : probe.Received;
        Log.Information(
            "health-stat probe provider={Provider} sample={Sample} found={Found} missing={Missing} " +
            "received={Received} ms={ElapsedMs} rate={StatRate}stat/s status={Status}",
            probe.Provider.Host, sampleSize, probe.Found, probe.Missing, probe.Received,
            probe.ElapsedMs, rate, probe.Status);
    }

    private static List<int> SelectProbeIndexes(int count, int targetCount)
    {
        var selected = Math.Min(count, targetCount);
        if (selected <= 1) return [0];
        return Enumerable.Range(0, selected)
            .Select(index => (int)((long)index * (count - 1) / (selected - 1)))
            .Distinct()
            .ToList();
    }

    public override async IAsyncEnumerable<PipelinedStatResult> StatsPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;

        for (var offset = 0; offset < segmentIds.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var orderedProviders = SelectOrderedProvidersForStat(cancellationToken, out var reserved);
            using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
            var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
            if (primary == null) throw new InvalidOperationException("There are no usenet providers configured.");

            var effectiveDepth = ResolveHealthDepth(primary, depth);
            var chunkSize = ResolvePipelinedChunkSize(effectiveDepth);
            var chunk = Slice(segmentIds, offset, chunkSize);
            var plan = BulkStatPlanContext.Value;
            if (plan is not null && !ReferenceEquals(plan.Owner, this)) plan = null;
            var results = await ResolvePipelinedStatBatchAsync(
                chunk, orderedProviders, depth, plan, cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
                yield return result;

            offset += chunk.Count;
        }
    }

    private static async Task<IReadOnlyList<PipelinedStatResult>> ResolvePipelinedStatBatchAsync(
        IReadOnlyList<string> segmentIds,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        int fallbackDepth,
        BulkStatPlan? plan,
        CancellationToken cancellationToken)
    {
        var resolved = new PipelinedStatResult?[segmentIds.Count];
        var pending = Enumerable.Range(0, segmentIds.Count).ToList();
        ExceptionDispatchInfo? lastException = null;
        var lastAttemptFailed = false;

        foreach (var provider in orderedProviders)
        {
            if (pending.Count == 0) break;
            cancellationToken.ThrowIfCancellationRequested();

            var attempted = pending;
            var batch = attempted.Select(index => segmentIds[index]).ToArray();
            var stillPending = new List<int>(attempted.Count);
            var received = 0;
            var found = 0;
            var missing = 0;
            lastAttemptFailed = false;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var providerDepth = ResolveHealthDepth(provider, fallbackDepth);
                await foreach (var result in provider.StatsPipelinedAsync(batch, providerDepth, cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (received >= attempted.Count)
                        throw new InvalidDataException(
                            $"Provider {provider.Host} returned too many pipelined STAT responses.");

                    var resultIndex = attempted[received];
                    var expectedSegmentId = segmentIds[resultIndex];
                    if (!string.Equals(result.SegmentId, expectedSegmentId, StringComparison.Ordinal))
                        throw new InvalidDataException(
                            $"Provider {provider.Host} returned an out-of-order pipelined STAT response.");

                    resolved[resultIndex] = result;
                    if (result.Exists)
                        found++;
                    else
                    {
                        missing++;
                        stillPending.Add(resultIndex);
                    }
                    received++;
                }

                if (received != attempted.Count)
                    throw new IOException(
                        $"Provider {provider.Host} ended a pipelined STAT batch after {received} of {attempted.Count} responses.");
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                lastAttemptFailed = true;
                lastException = ExceptionDispatchInfo.Capture(e);
                for (var i = received; i < attempted.Count; i++)
                    stillPending.Add(attempted[i]);
                Log.Debug(e,
                    "Pipelined STAT batch failed on provider {Provider}; retrying {Count} unresolved segments as a batch.",
                    provider.Host, stillPending.Count);
            }
            finally
            {
                plan?.RecordAttempt(provider, attempted.Count, received, found, missing,
                    stopwatch.ElapsedMilliseconds, lastAttemptFailed);
            }

            pending = stillPending;
        }

        if (pending.Count > 0 && lastAttemptFailed)
        {
            lastException?.Throw();
            throw new IOException("Pipelined STAT batch failed without a provider error.");
        }

        for (var i = 0; i < resolved.Length; i++)
        {
            resolved[i] ??= new PipelinedStatResult
            {
                SegmentId = segmentIds[i],
                Exists = false,
            };
        }

        return resolved!;
    }

    public override async IAsyncEnumerable<PipelinedBodyResult> DecodedBodiesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(cancellationToken, out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var failedProviders = new HashSet<MultiConnectionNntpClient>();

        for (var offset = 0; offset < segmentIds.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveDepth = ResolveBodyDepth(primary, depth);
            var chunkSize = ResolvePipelinedChunkSize(effectiveDepth);
            var chunk = Slice(segmentIds, offset, chunkSize);
            var nextIndex = 0;
            var rescueFrom = chunk.Count;
            var rotatePrimary = false;

            await using (var enumerator = primary
                             .DecodedBodiesPipelinedAsync(chunk, effectiveDepth, cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (nextIndex < chunk.Count)
                {
                    PipelinedBodyResult result;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            rotatePrimary = true;
                            rescueFrom = nextIndex;
                            break;
                        }
                        result = enumerator.Current;
                    }
                    catch (Exception e) when (!e.IsCancellationException())
                    {
                        Log.Debug(e, "Pipelined BODY chunk failed on primary provider {Provider}; rescuing remaining segments.",
                            primary.Host);
                        rotatePrimary = true;
                        rescueFrom = nextIndex;
                        break;
                    }

                    if (result.Found)
                    {
                        nextIndex++;
                        usageTracker.RecordSuccess(primary.Id);
                        yield return WrapPipelinedBody(result, primary.Id);
                        continue;
                    }

                    rescueFrom = nextIndex;
                    break;
                }

                if (nextIndex == chunk.Count && rescueFrom == chunk.Count)
                {
                    try
                    {
                        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                            throw new IOException("Pipelined BODY chunk returned too many responses.");
                    }
                    catch (Exception e) when (!e.IsCancellationException())
                    {
                        Log.Debug(e, "Pipelined BODY chunk failed on primary provider {Provider} after its final response.",
                            primary.Host);
                        rotatePrimary = true;
                    }
                }

                if (nextIndex < chunk.Count && rescueFrom == chunk.Count)
                    rescueFrom = nextIndex;
            }

            if (rotatePrimary)
                primary = SelectReplacementPipelinedProvider(
                    primary, failedProviders, cancellationToken, ref reserved);

            for (var rescueIndex = rescueFrom; rescueIndex < chunk.Count; rescueIndex++)
            {
                var segmentId = chunk[rescueIndex];
                yield return await RescuePipelinedBody(
                    segmentId,
                    new PipelinedBodyResult { SegmentId = segmentId, Found = false, Stream = null },
                    cancellationToken).ConfigureAwait(false);
            }

            offset += chunk.Count;
        }
    }

    public override async IAsyncEnumerable<PipelinedArticleResult> DecodedArticlesPipelinedAsync(
        IReadOnlyList<string> segmentIds, int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0) yield break;
        var orderedProviders = SelectOrderedProviders(cancellationToken, out var reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var primary = orderedProviders.Count > 0 ? orderedProviders[0] : null;
        if (primary == null) yield break;
        var failedProviders = new HashSet<MultiConnectionNntpClient>();

        for (var offset = 0; offset < segmentIds.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveDepth = ResolveBodyDepth(primary, depth);
            var chunkSize = ResolvePipelinedChunkSize(effectiveDepth);
            var chunk = Slice(segmentIds, offset, chunkSize);
            var nextIndex = 0;
            var rescueFrom = chunk.Count;
            var rotatePrimary = false;

            await using (var enumerator = primary
                             .DecodedArticlesPipelinedAsync(chunk, effectiveDepth, cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (nextIndex < chunk.Count)
                {
                    PipelinedArticleResult result;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            rotatePrimary = true;
                            rescueFrom = nextIndex;
                            break;
                        }
                        result = enumerator.Current;
                    }
                    catch (Exception e) when (!e.IsCancellationException())
                    {
                        Log.Debug(e, "Pipelined ARTICLE chunk failed on primary provider {Provider}; rescuing remaining segments.",
                            primary.Host);
                        rotatePrimary = true;
                        rescueFrom = nextIndex;
                        break;
                    }

                    if (result.Found)
                    {
                        nextIndex++;
                        usageTracker.RecordSuccess(primary.Id);
                        yield return WrapPipelinedArticle(result, primary.Id);
                        continue;
                    }

                    rescueFrom = nextIndex;
                    break;
                }

                if (nextIndex == chunk.Count && rescueFrom == chunk.Count)
                {
                    try
                    {
                        if (await enumerator.MoveNextAsync().ConfigureAwait(false))
                            throw new IOException("Pipelined ARTICLE chunk returned too many responses.");
                    }
                    catch (Exception e) when (!e.IsCancellationException())
                    {
                        Log.Debug(e, "Pipelined ARTICLE chunk failed on primary provider {Provider} after its final response.",
                            primary.Host);
                        rotatePrimary = true;
                    }
                }

                if (nextIndex < chunk.Count && rescueFrom == chunk.Count)
                    rescueFrom = nextIndex;
            }

            if (rotatePrimary)
                primary = SelectReplacementPipelinedProvider(
                    primary, failedProviders, cancellationToken, ref reserved);

            for (var rescueIndex = rescueFrom; rescueIndex < chunk.Count; rescueIndex++)
            {
                var segmentId = chunk[rescueIndex];
                yield return await RescuePipelinedArticle(
                    segmentId,
                    new PipelinedArticleResult { SegmentId = segmentId, Found = false },
                    cancellationToken).ConfigureAwait(false);
            }

            offset += chunk.Count;
        }
    }

    private async Task<PipelinedBodyResult> RescuePipelinedBody(
        string segmentId,
        PipelinedBodyResult fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var rescued = await DecodedBodyAsync(segmentId, cancellationToken).ConfigureAwait(false);
            return new PipelinedBodyResult
            {
                SegmentId = segmentId,
                Found = true,
                Stream = rescued.Stream,
            };
        }
        catch (UsenetArticleNotFoundException)
        {
            return fallback;
        }
    }

    private async Task<PipelinedArticleResult> RescuePipelinedArticle(
        string segmentId,
        PipelinedArticleResult fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var rescued = await DecodedArticleAsync(segmentId, cancellationToken).ConfigureAwait(false);
            return new PipelinedArticleResult
            {
                SegmentId = segmentId,
                Found = true,
                Stream = rescued.Stream,
                ArticleHeaders = rescued.ArticleHeaders,
            };
        }
        catch (UsenetArticleNotFoundException)
        {
            return fallback;
        }
    }

    private static int ResolvePipelinedChunkSize(int depth) => Math.Clamp(depth, 1, 64);

    private static List<string> Slice(IReadOnlyList<string> items, int offset, int maxCount)
    {
        var count = Math.Min(maxCount, items.Count - offset);
        var result = new List<string>(count);
        for (var i = 0; i < count; i++) result.Add(items[offset + i]);
        return result;
    }

    private PipelinedBodyResult WrapPipelinedBody(PipelinedBodyResult result, string host)
    {
        if (bytesTracker == null || result.Stream == null) return result;
        return result with
        {
            Stream = new CountingYencStream(
                result.Stream, bytesTracker, host, bytes => usageTracker.RecordBytes(host, bytes))
        };
    }

    private PipelinedArticleResult WrapPipelinedArticle(PipelinedArticleResult result, string host)
    {
        if (bytesTracker == null || result.Stream == null) return result;
        return result with
        {
            Stream = new CountingYencStream(
                result.Stream, bytesTracker, host, bytes => usageTracker.RecordBytes(host, bytes))
        };
    }

    private sealed record BulkStatQualification(BulkStatPlan? Plan, HashSet<int> VerifiedIndexes)
    {
        public static readonly BulkStatQualification None = new(null, []);
    }

    private sealed record BulkStatProbe(
        MultiConnectionNntpClient Provider,
        bool[] Exists,
        int Received,
        int Found,
        int Missing,
        long ElapsedMs,
        bool Success,
        string Status)
    {
        public double Rate => Received == 0
            ? 0
            : Received * 1000d / Math.Max(1, ElapsedMs);
    }

    private sealed class BulkStatPlan
    {
        private readonly ConcurrentDictionary<MultiConnectionNntpClient, BulkStatProbe> _probes;
        private readonly Dictionary<MultiConnectionNntpClient, int> _probeRanks;
        private readonly HashSet<MultiConnectionNntpClient> _preferred;
        private readonly ConcurrentDictionary<MultiConnectionNntpClient, BulkStatAttemptStats> _attempts = new();
        private HealthConnectionAllocation? _connectionAllocation;

        public BulkStatPlan(
            MultiProviderNntpClient owner,
            IReadOnlyList<BulkStatProbe> probes,
            HashSet<MultiConnectionNntpClient> preferred)
        {
            Owner = owner;
            _probes = new ConcurrentDictionary<MultiConnectionNntpClient, BulkStatProbe>(
                probes.ToDictionary(x => x.Provider));
            _preferred = new HashSet<MultiConnectionNntpClient>(preferred);
            var ranked = probes
                .OrderByDescending(x => x.Success)
                .ThenByDescending(x => x.Found)
                .ThenByDescending(x => x.Rate)
                .ThenBy(x => x.Provider.Priority)
                .Select((probe, index) => new { probe.Provider, Rank = index });
            _probeRanks = ranked.ToDictionary(x => x.Provider, x => x.Rank);
        }

        public MultiProviderNntpClient Owner { get; }

        public IDisposable BeginConnectionAllocation(int connectionDemand)
        {
            var allocation = new HealthConnectionAllocation(Owner, this, connectionDemand);
            var existing = Interlocked.CompareExchange(
                ref _connectionAllocation, allocation, null);
            if (existing is not null)
            {
                allocation.Dispose();
                return existing;
            }

            allocation.Reconcile();
            return allocation;
        }

        public void ObserveLateProbes(
            IReadOnlyList<Task<BulkStatProbe>> probeTasks,
            int sampleSize,
            CancellationToken cancellationToken)
        {
            foreach (var probeTask in probeTasks)
                _ = ObserveLateProbeAsync(probeTask, sampleSize, cancellationToken);
        }

        private async Task ObserveLateProbeAsync(
            Task<BulkStatProbe> probeTask,
            int sampleSize,
            CancellationToken cancellationToken)
        {
            try
            {
                var probe = await probeTask.ConfigureAwait(false);
                LogBulkStatProbe(probe, sampleSize);
                AddProbe(probe);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void AddProbe(BulkStatProbe probe)
        {
            _probes[probe.Provider] = probe;
            lock (_preferred)
            {
                var preferred = Owner.SelectPreferredStatProviders(_probes.Values);
                _preferred.Clear();
                _preferred.UnionWith(preferred);
            }
            Volatile.Read(ref _connectionAllocation)?.Reconcile();
        }

        public (MultiConnectionNntpClient[] Probed, HashSet<MultiConnectionNntpClient> Preferred)
            SnapshotAllocationState()
        {
            var probed = _probes.Keys.ToArray();
            lock (_preferred)
                return (probed, new HashSet<MultiConnectionNntpClient>(_preferred));
        }

        public bool IsPreferred(MultiConnectionNntpClient provider)
        {
            lock (_preferred) return _preferred.Contains(provider);
        }

        public int ProbeRank(MultiConnectionNntpClient provider) =>
            _probeRanks.GetValueOrDefault(provider, int.MaxValue);

        public double Coverage(MultiConnectionNntpClient provider)
        {
            var probe = _probes.GetValueOrDefault(provider);
            return probe is { Received: > 0 } ? probe.Found / (double)probe.Received : 0;
        }

        public double SelectionScore(MultiConnectionNntpClient provider)
        {
            var capacity = Math.Max(1, provider.ActiveConnections + provider.AvailableConnections);
            var committed = provider.ActiveConnections + provider.PendingSelections;
            var rate = EffectiveRate(provider);
            MultiConnectionNntpClient[] preferred;
            lock (_preferred) preferred = _preferred.ToArray();
            var fastestRate = preferred.Select(EffectiveRate).DefaultIfEmpty(1).Max();
            var speedWeight = Math.Clamp(rate / Math.Max(1, fastestRate), 0.01, 1);
            return (committed + 1d) / (capacity * speedWeight);
        }

        public void RecordAttempt(
            MultiConnectionNntpClient provider,
            int attempted,
            int received,
            int found,
            int missing,
            long elapsedMs,
            bool failed)
        {
            _attempts.GetOrAdd(provider, static _ => new BulkStatAttemptStats())
                .Record(attempted, received, found, missing, elapsedMs, failed);
        }

        private double EffectiveRate(MultiConnectionNntpClient provider)
        {
            var probe = _probes.GetValueOrDefault(provider);
            var probeRate = probe?.Rate ?? 0;
            if (!_attempts.TryGetValue(provider, out var attempts)) return probeRate;

            var snapshot = attempts.Snapshot();
            if (snapshot.Received == 0 || snapshot.ElapsedMs == 0) return probeRate;
            var liveRate = snapshot.Received * 1000d / snapshot.ElapsedMs;
            var liveWeight = Math.Clamp(snapshot.Received / 256d, 0, 1);
            var blendedRate = probeRate * (1 - liveWeight) + liveRate * liveWeight;
            return snapshot.Failures == 0 ? blendedRate : blendedRate / (snapshot.Failures + 1);
        }

        public void LogSummary()
        {
            var attemptedProviders = _probes.Keys
                .Concat(_attempts.Keys)
                .Distinct()
                .OrderBy(ProbeRank)
                .ThenBy(x => x.Priority)
                .ToArray();
            foreach (var provider in attemptedProviders)
            {
                _probes.TryGetValue(provider, out var probe);
                _attempts.TryGetValue(provider, out var attempts);
                var snapshot = attempts?.Snapshot() ?? default;
                var rate = snapshot.Received > 0 && snapshot.ElapsedMs > 0
                    ? (int)Math.Round(snapshot.Received * 1000d / snapshot.ElapsedMs)
                    : probe is { Received: > 0, ElapsedMs: > 0 }
                        ? (int)Math.Round(probe.Received * 1000d / probe.ElapsedMs)
                        : 0;
                Log.Information(
                    "health-stat provider-summary provider={Provider} preferred={Preferred} " +
                    "probeFound={ProbeFound}/{ProbeReceived} batches={Batches} attempted={Attempted} received={Received} " +
                    "found={Found} missing={Missing} failures={Failures} laneMs={LaneMs} " +
                    "laneRate={StatRate}stat/s",
                    provider.Host, IsPreferred(provider), probe?.Found ?? 0, probe?.Received ?? 0,
                    snapshot.Batches, snapshot.Attempted, snapshot.Received, snapshot.Found, snapshot.Missing,
                    snapshot.Failures, snapshot.ElapsedMs, rate);
                Owner.RecordHealthProviderStat(provider, probe, snapshot, rate, IsPreferred(provider));
            }
        }
    }

    private sealed class HealthConnectionAllocation(
        MultiProviderNntpClient owner,
        BulkStatPlan plan,
        int connectionDemand) : IDisposable
    {
        private readonly object _lock = new();
        private readonly Dictionary<MultiConnectionNntpClient, IDisposable> _suspensions = [];
        private int _reclaimed;
        private bool _disposed;

        public void Reconcile()
        {
            lock (_lock)
            {
                if (_disposed) return;

                var (probed, preferred) = plan.SnapshotAllocationState();
                // With no confirmed coverage, retain every provider for the
                // normal fallback search rather than guessing which pool to cut.
                if (preferred.Count == 0) return;

                foreach (var provider in probed.Where(preferred.Contains))
                {
                    if (_suspensions.Remove(provider, out var suspension))
                    {
                        suspension.Dispose();
                        _ = RestoreWarmProviderAsync(provider);
                    }
                }

                var usefulTarget = Math.Min(
                    Math.Max(1, connectionDemand),
                    preferred.Sum(provider => provider.MaxConnections));
                var preferredLive = preferred.Sum(provider => provider.LiveConnections);
                var totalLive = owner.TotalLiveConnections;
                var globalHeadroom = Math.Max(0, owner.ApplicationConnectionLimit - totalLive);
                var reclaimNeeded = Math.Max(0, usefulTarget - preferredLive - globalHeadroom);

                foreach (var provider in probed
                             .Where(provider => !preferred.Contains(provider))
                             .OrderByDescending(provider => provider.IdleConnections))
                {
                    if (_suspensions.ContainsKey(provider)) continue;
                    try
                    {
                        var reclaimable = Math.Max(
                            0, provider.IdleConnections - HealthReclamationIdleFloor);
                        var requested = Math.Min(reclaimNeeded, reclaimable);
                        var retained = provider.IdleConnections - requested;
                        var suspension = provider.SuspendPrewarming(retained, out var reclaimed);
                        _suspensions.Add(provider, suspension);
                        _reclaimed += reclaimed;
                        reclaimNeeded = Math.Max(0, reclaimNeeded - reclaimed);
                        Log.Information(
                            "health-stat allocation provider={Provider} action=reclaim retained={Retained} " +
                            "reclaimed={Reclaimed} remainingDemand={RemainingDemand} live={Live} idle={Idle}",
                            provider.Host, retained, reclaimed, reclaimNeeded,
                            provider.LiveConnections, provider.IdleConnections);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Provider reload can retire this graph while a late
                        // probe joins it. The replacement graph owns new pools.
                    }
                }
            }
        }

        public void Dispose()
        {
            MultiConnectionNntpClient[] restore;
            int reclaimed;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                restore = _suspensions.Keys.ToArray();
                foreach (var suspension in _suspensions.Values)
                    suspension.Dispose();
                _suspensions.Clear();
                reclaimed = _reclaimed;
            }

            foreach (var provider in restore)
                _ = RestoreWarmProviderAsync(provider);

            Log.Information(
                "health-stat allocation action=restore providers={Providers} reclaimed={Reclaimed} blocking=False",
                restore.Length, reclaimed);
        }

        private static async Task RestoreWarmProviderAsync(MultiConnectionNntpClient provider)
        {
            var target = UsenetStreamingClient.GetWarmConnectionTarget(
                provider.ProviderType, provider.MaxConnections);
            if (target == 0) return;

            try
            {
                await provider.PrewarmAsync(target, SigtermUtil.GetCancellationToken())
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                SigtermUtil.GetCancellationToken().IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Log.Debug(e,
                    "Could not restore warm NNTP connections for provider={Provider} target={Target}",
                    provider.Host, target);
            }
        }
    }

    private void RecordHealthProviderStat(
        MultiConnectionNntpClient provider,
        BulkStatProbe? probe,
        BulkStatAttemptSnapshot snapshot,
        long rate,
        bool preferred)
    {
        usageTracker.RecordHealthProviderStat(new HealthProviderStat(
            provider.Id,
            provider.Host,
            preferred,
            probe?.Found ?? 0,
            probe?.Received ?? 0,
            snapshot.Batches,
            snapshot.Attempted,
            snapshot.Received,
            snapshot.Found,
            snapshot.Missing,
            snapshot.Failures,
            snapshot.ElapsedMs,
            rate,
            probe?.Status));
    }

    internal sealed class BulkStatAttemptStats
    {
        private long _attempted;
        private long _batches;
        private long _elapsedMs;
        private long _failures;
        private long _found;
        private long _missing;
        private long _received;

        public void Record(
            int attempted,
            int received,
            int found,
            int missing,
            long elapsedMs,
            bool failed)
        {
            Interlocked.Increment(ref _batches);
            Interlocked.Add(ref _attempted, attempted);
            Interlocked.Add(ref _received, received);
            Interlocked.Add(ref _found, found);
            Interlocked.Add(ref _missing, missing);
            Interlocked.Add(ref _elapsedMs, elapsedMs);
            if (failed) Interlocked.Increment(ref _failures);
        }

        public BulkStatAttemptSnapshot Snapshot() => new(
            Interlocked.Read(ref _batches),
            Interlocked.Read(ref _attempted),
            Interlocked.Read(ref _received),
            Interlocked.Read(ref _found),
            Interlocked.Read(ref _missing),
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _elapsedMs));
    }

    internal readonly record struct BulkStatAttemptSnapshot(
        long Batches,
        long Attempted,
        long Received,
        long Found,
        long Missing,
        long Failures,
        long ElapsedMs);

    private sealed class OffsetProgress(IProgress<int> target, int offset) : IProgress<int>
    {
        public void Report(int value) => target.Report(offset + value);
    }

    public override void Dispose()
    {
        foreach (var provider in providers)
            provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
