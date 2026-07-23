using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Connections;
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
    Func<int>? warmValidationConnectionBudget = null,
    TimeSpan? providerAttemptTimeout = null,
    TimeSpan? providerOperationTimeout = null,
    ConnectionLifetimeBudget? connectionBudget = null,
    TimeSpan? indeterminateRecoveryBudget = null,
    TimeSpan? bulkStatProbeTimeout = null,
    TimeSpan? pipelinedStatResponseInactivityTimeout = null
) : NntpClient, IQueueConnectionWarmer
{
    private const int HealthPrimeConnectionLimitPerProvider = 4;
    private const int BulkStatProbeSize = 32;
    private const int BulkStatProbeThreshold = 256;
    private static readonly TimeSpan HealthConnectionPrimeTimeout = TimeSpan.FromSeconds(5);
    private const int HealthReclamationIdleFloor = 4;
    private const int HealthQuarantinedRecoveryIdleFloor = 8;
    private const int HealthRecoveryConnectionReserve = 4;
    private const int HealthRecoveryMaxConnectionsPerProvider = 8;
    private const int HealthRecoverySegmentsPerConnection = 512;
    private const double PartialStatMinimumCoverage = 0.50;
    private static readonly TimeSpan BulkStatProbeJoinWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BulkStatProbeCapacitySettleWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BulkStatProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultProviderAttemptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultProviderOperationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultPipelinedStatResponseInactivityTimeout =
        TimeSpan.FromMilliseconds(1500);
    private const int StatRecoveryConcurrencyLimit = 4;
    private const int HealthLaneGrowthHeadroom =
        UsenetStreamingClient.ConcurrentConnectionAttemptLimit;
    private static readonly TimeSpan StatRecoveryAdmissionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultIndeterminateRecoveryBudget = TimeSpan.FromSeconds(15);
    private const int QuarantineConsecutiveFailureThreshold = 2;
    private readonly SemaphoreSlim _statRecoveryGate = new(
        StatRecoveryConcurrencyLimit, StatRecoveryConcurrencyLimit);
    private static readonly AsyncLocal<Guid?> ReadSessionScope = new();
    private static readonly AsyncLocal<BulkStatPlan?> BulkStatPlanContext = new();
    private static readonly AsyncLocal<StatVerdictCollector?> VerdictCollectorContext = new();
    private readonly BackupRecoveryCoordinator _backupRecovery = new(
        HealthRecoveryConnectionReserve);

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

    internal void UpdateConnectionPriorityOdds(SemaphorePriorityOdds priorityOdds)
    {
        foreach (var provider in providers)
            provider.UpdatePriorityOdds(priorityOdds);
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
    private IReadOnlyList<MultiConnectionNntpClient> Providers => providers;
    private ConnectionLifetimeBudget? ConnectionBudget => connectionBudget;
    private int TotalLiveConnections => providers.Sum(provider => provider.LiveConnections);
    private int ApplicationConnectionLimit => applicationConnectionLimit;
    private int WarmValidationConnectionBudget => Math.Clamp(
        warmValidationConnectionBudget?.Invoke() ?? 384,
        0,
        ApplicationConnectionLimit);
    private TimeSpan ProviderAttemptTimeout => providerAttemptTimeout ?? DefaultProviderAttemptTimeout;
    private TimeSpan ProviderOperationTimeout => providerOperationTimeout ?? DefaultProviderOperationTimeout;
    private TimeSpan PipelinedStatResponseInactivityTimeout =>
        pipelinedStatResponseInactivityTimeout ?? DefaultPipelinedStatResponseInactivityTimeout;
    private TimeSpan BulkProbeTimeout => bulkStatProbeTimeout ?? BulkStatProbeTimeout;
    private TimeSpan IndeterminateRecoveryBudget =>
        indeterminateRecoveryBudget ?? DefaultIndeterminateRecoveryBudget;

    internal void ActivateIdlePrewarming()
    {
        var idleTarget = 0;
        var warmTarget = 0;
        var activated = 0;
        foreach (var provider in providers)
        {
            var providerTarget = provider.PersistentIdleConnectionTarget;
            if (providerTarget <= 0) continue;
            provider.ActivateIdlePrewarming();
            idleTarget += providerTarget;
            warmTarget += provider.PersistentWarmConnectionTarget;
            activated++;
        }

        Log.Information(
            "usenet-startup-prewarm stage=activated providers={Providers} " +
            "idleTarget={IdleTarget} warmTarget={WarmTarget}",
            activated, idleTarget, warmTarget);
    }

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
                    await allocation.Key.PrewarmForDemandAsync(
                            allocation.Value, cancellationToken)
                        .ConfigureAwait(false);
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
        // Pool providers are already warmed by queue prewarm and then exercised by
        // BODY/ARTICLE prep. Health-only and backup+STAT providers otherwise sit
        // outside that path, so establish the same bounded warm floor used at
        // startup while prep is busy. Do not force-validate established idle
        // connections here: borrowing the entire idle pool for DATE can make it
        // unavailable to the foreground prep that triggered this warmup.
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
                await provider.PrewarmForDemandAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                Log.Debug(
                    "Health prewarm provider={Provider} target={Target} mode=establish-only " +
                    "warm={Warm} live={Live} idle={Idle}",
                    provider.Host, target,
                    provider.WarmConnections, provider.LiveConnections, provider.IdleConnections);
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
        // First-segment prep intentionally reads only the metadata prefix of each
        // article. Those partially consumed BODY connections are discarded to keep
        // the NNTP command stream synchronized, so a Primary pool can be cold at the
        // prep-to-STAT boundary even when queue prewarm filled it beforehand.
        // Refill opportunistically after that boundary, but leave every established
        // idle socket available to lazy-RAR and other foreground ARTICLE work.
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
                await provider.PrewarmForDemandAsync(target, cancellationToken)
                    .ConfigureAwait(false);
                Log.Debug(
                    "Primary health prewarm provider={Provider} target={Target} mode=establish-only " +
                    "warm={Warm} live={Live} idle={Idle}",
                    provider.Host, target,
                    provider.WarmConnections, provider.LiveConnections, provider.IdleConnections);
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
        var validationAllocations = GetWarmValidationAllocations();
        var effectiveDepth = Math.Clamp(depth, 1, segmentIds.Count);
        var eligible = candidates
            .Where(provider => !provider.IsTripped)
            .Where(provider => !IsOverLimit(provider))
            .ToArray();

        var probes = await Task.WhenAll(eligible.Select(provider =>
            ProbeHealthPrimeProviderAsync(
                provider, segmentIds, effectiveDepth, phase, cancellationToken)));
        var successful = probes
            .Where(probe => probe.Success && probe.Found > 0)
            .ToArray();
        if (successful.Length == 0) return;

        var bestCoverage = successful.Max(probe => probe.Found);
        var preferred = successful
            .Where(probe => probe.Found == bestCoverage ||
                            IsPartialStatProviderEligible(probe.Found, probe.Received))
            .Select(probe => probe.Provider)
            .ToHashSet();

        await Task.WhenAll(eligible.Where(preferred.Contains).Select(async provider =>
        {
            // The probe established that this provider is viable. Exercise a small
            // sample of distinct sockets on the real STAT path. Priming an entire
            // large pool can consume the capacity needed by foreground ARTICLE work
            // and turns one slow provider into a job-wide timeout burst.
            var allocatedConcurrency = validationAllocations.GetValueOrDefault(provider);
            var concurrency = Math.Min(
                HealthPrimeConnectionLimitPerProvider, allocatedConcurrency);
            var connectionCount = Math.Min(provider.IdleConnections, concurrency);
            if (connectionCount == 0) return;
            var timer = Stopwatch.StartNew();
            using var primeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            primeCts.CancelAfter(HealthConnectionPrimeTimeout);
            try
            {
                await provider.PrimeHealthConnectionsAsync(
                        segmentIds, connectionCount, concurrency, primeCts.Token)
                    .ConfigureAwait(false);
                Log.Debug(
                    "Health STAT prime phase={Phase} provider={Provider} connections={Connections} " +
                    "commandsPerConnection={CommandsPerConnection} ms={ElapsedMs}",
                    phase, provider.Host, connectionCount, 1, timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && primeCts.IsCancellationRequested)
            {
                Log.Debug(
                    "Health STAT prime timed out phase={Phase} provider={Provider} " +
                    "connections={Connections} commandsPerConnection={CommandsPerConnection} ms={ElapsedMs}",
                    phase, provider.Host, connectionCount, 1, timer.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                Log.Debug(e,
                    "Health STAT prime failed phase={Phase} provider={Provider} " +
                    "connections={Connections} commandsPerConnection={CommandsPerConnection} ms={ElapsedMs}",
                    phase, provider.Host, connectionCount, 1, timer.ElapsedMilliseconds);
            }
        })).ConfigureAwait(false);
    }

    private Dictionary<MultiConnectionNntpClient, int> GetWarmValidationAllocations()
    {
        var eligible = providers
            .Where(provider => provider.ProviderType is ProviderType.Pooled
                or ProviderType.BackupAndStats
                or ProviderType.HealthChecksOnly)
            .Where(provider => !provider.IsTripped)
            .Where(provider => !IsOverLimit(provider))
            .ToArray();
        var targets = eligible
            .Select(provider => UsenetStreamingClient.GetHealthCheckWarmConnectionTarget(
                provider.ProviderType, provider.MaxConnections))
            .ToArray();
        var allocations = AllocateWarmValidationBudget(targets, WarmValidationConnectionBudget);
        return eligible
            .Select((provider, index) => new { Provider = provider, Allocation = allocations[index] })
            .ToDictionary(item => item.Provider, item => item.Allocation);
    }

    internal static int[] AllocateWarmValidationBudget(IReadOnlyList<int> targets, int requestedBudget)
    {
        var normalized = targets.Select(target => Math.Max(0, target)).ToArray();
        var total = normalized.Sum(target => (long)target);
        if (total == 0 || requestedBudget <= 0) return new int[normalized.Length];

        var budget = (int)Math.Min(total, requestedBudget);
        var allocations = new int[normalized.Length];
        var remainders = new (int Index, double Remainder)[normalized.Length];
        var allocated = 0;
        for (var index = 0; index < normalized.Length; index++)
        {
            var exact = budget * (double)normalized[index] / total;
            allocations[index] = Math.Min(normalized[index], (int)Math.Floor(exact));
            allocated += allocations[index];
            remainders[index] = (index, exact - allocations[index]);
        }

        foreach (var item in remainders
                     .OrderByDescending(item => item.Remainder)
                     .ThenByDescending(item => normalized[item.Index])
                     .ThenBy(item => item.Index))
        {
            if (allocated >= budget) break;
            if (allocations[item.Index] >= normalized[item.Index]) continue;
            allocations[item.Index]++;
            allocated++;
        }

        return allocations;
    }

    private static async Task<HealthPrimeProbe> ProbeHealthPrimeProviderAsync(
        MultiConnectionNntpClient provider,
        IReadOnlyList<string> segmentIds,
        int depth,
        string phase,
        CancellationToken cancellationToken)
    {
        var received = 0;
        var found = 0;
        var timer = Stopwatch.StartNew();
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(HealthConnectionPrimeTimeout);
        try
        {
            (received, found) = await provider.ProbeHealthCoverageAsync(
                    segmentIds, ResolveHealthDepth(provider, depth), probeCts.Token)
                .ConfigureAwait(false);
            Log.Debug(
                "Health warm probe phase={Phase} provider={Provider} found={Found}/{Received} " +
                "ms={ElapsedMs} status={Status}",
                phase, provider.Host, found, received, timer.ElapsedMilliseconds, "ok");
            return new HealthPrimeProbe(provider, received, found, true);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && probeCts.IsCancellationRequested)
        {
            Log.Debug(
                "Health warm probe timed out phase={Phase} provider={Provider} " +
                "found={Found}/{Received} ms={ElapsedMs}",
                phase, provider.Host, found, received, timer.ElapsedMilliseconds);
            return new HealthPrimeProbe(provider, received, found, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Debug(e,
                "Health warm probe failed phase={Phase} provider={Provider} " +
                "found={Found}/{Received} ms={ElapsedMs}",
                phase, provider.Host, found, received, timer.ElapsedMilliseconds);
            return new HealthPrimeProbe(provider, received, found, false);
        }
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
            (x, attemptToken) => x.DecodedArticleAsync(segmentId, attemptToken),
            cancellationToken,
            prepFallbackProbe: (x, attemptToken) =>
                VerifyArticleExistsAsync(x, segmentId, attemptToken));
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
                cancellationToken,
                prepFallbackProbe: (x, attemptToken) =>
                    VerifyArticleExistsAsync(x, segmentId, attemptToken)
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
        bool statOperation = false,
        Func<INntpClient, CancellationToken, Task>? prepFallbackProbe = null
    ) where T : UsenetResponse
    {
        var attribution = AttributionContext.Value;
        if (attribution != null) attribution.Host = null;
        ExceptionDispatchInfo? lastException = null;
        List<(string Host, SegmentFetch.FetchStatus Reason)>? priorMisses = null;
        var attemptedProviders = 0;
        var providerCheckIncomplete = false;
        var prepFallback = cancellationToken.GetContext<PrepFallbackContext>();
        var orderedProviders = statOperation
            ? SelectOrderedProvidersForStat(cancellationToken, out var reserved)
            : SelectOrderedProviders(cancellationToken, out reserved);
        using var releasePending = new ScopeReleaser(() => reserved?.ReleasePending());
        var operationStopwatch = Stopwatch.StartNew();
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingOperationTime = ProviderOperationTimeout - operationStopwatch.Elapsed;
            if (prepFallback is null && remainingOperationTime <= TimeSpan.Zero)
            {
                providerCheckIncomplete = true;
                break;
            }
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;
            PrepFallbackContext.PrepFallbackLease? fallbackAdmission = null;

            if (prepFallback is not null && i > 0)
            {
                usageTracker.ReportRecoveryNotice(
                    new QueueRecoveryNotice("prep", 0));
                try
                {
                    fallbackAdmission = await prepFallback.EnterAsync(
                        provider, cancellationToken).ConfigureAwait(false);
                    if (fallbackAdmission is null)
                    {
                        providerCheckIncomplete = true;
                        lastException ??= ExceptionDispatchInfo.Capture(new TimeoutException(
                            $"Provider {provider.Host} was skipped after an earlier " +
                            $"fallback attempt failed during this preparation run."));
                        continue;
                    }
                }
                catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
                {
                    providerCheckIncomplete = true;
                    lastException = ExceptionDispatchInfo.Capture(new TimeoutException(
                        $"Preparation was cancelled before provider {provider.Host} " +
                        "could be attempted.", e));
                    break;
                }
            }

            attemptedProviders++;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            var phaseSplitFallback = fallbackAdmission is not null;
            var providerStopwatch = Stopwatch.StartNew();
            remainingOperationTime = ProviderOperationTimeout - operationStopwatch.Elapsed;
            var maximumAttemptTime = prepFallback is not null
                ? ProviderAttemptTimeout
                : isLastProvider
                ? ProviderOperationTimeout
                : ProviderAttemptTimeout;
            var attemptTimeout = prepFallback is not null
                ? maximumAttemptTime
                : maximumAttemptTime < remainingOperationTime
                ? maximumAttemptTime
                : remainingOperationTime;
            var attemptCts =
                ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var attemptLifetimeTransferred = false;
            if (phaseSplitFallback)
            {
                attemptCts.SetContext(new ProviderAttemptContext(
                    attemptTimeout,
                    attemptTimeout,
                    () => attemptCts.CancelAfter(attemptTimeout)));
            }
            else
                attemptCts.CancelAfter(attemptTimeout);
            CancellationTokenRegistration hostFailureRegistration = default;
            if (phaseSplitFallback)
            {
                hostFailureRegistration = fallbackAdmission!.HostFailureToken.Register(() =>
                {
                    try
                    {
                        attemptCts.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The response won the race and detached from host-level
                        // fail-fast cancellation before the callback ran.
                    }
                });
            }
            var checkingPrepAvailability =
                phaseSplitFallback && prepFallbackProbe is not null;
            try
            {
                if (checkingPrepAvailability)
                {
                    await prepFallbackProbe!(provider, attemptCts.Token)
                        .ConfigureAwait(false);
                    checkingPrepAvailability = false;
                    // STAT and ARTICLE are independent NNTP commands. Stop
                    // STAT's command timer before ARTICLE waits for its own
                    // connection; MultiConnection arms a fresh command
                    // window after that connection has been acquired.
                    attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                var result = await task.Invoke(provider, attemptCts.Token).ConfigureAwait(false);
                providerStopwatch.Stop();
                // Once ARTICLE/BODY has returned a stream, do not let a timeout
                // on another lane abort this successfully-started body. Its own
                // caller/operation cancellation still remains linked.
                hostFailureRegistration.Dispose();
                if (phaseSplitFallback)
                    prepFallback!.MarkResponsive(provider);

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    RecordFetch(provider.Id, SegmentFetch.FetchStatus.Missing,
                        providerStopwatch.ElapsedMilliseconds, i);
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
                    RecordFetch(provider.Id, SegmentFetch.FetchStatus.Ok,
                        providerStopwatch.ElapsedMilliseconds, i);
                    if (i > 0)
                    {
                        usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, rescuer: provider.Id);
                    }
                    result = WrapStreamForByteCounting(result, provider.Id);
                }
                else
                {
                    RecordFetch(provider.Id, SegmentFetch.FetchStatus.Missing,
                        providerStopwatch.ElapsedMilliseconds, i);
                }

                result = PreserveCallerCancellationForStreamingResult(
                    result, attemptCts, fallbackAdmission, out attemptLifetimeTransferred);
                if (attemptLifetimeTransferred) fallbackAdmission = null;
                return result;
            }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                providerStopwatch.Stop();
                providerCheckIncomplete = true;
                var operationDescription = checkingPrepAvailability
                    ? "preparation availability check"
                    : prepFallback is not null
                        ? "preparation article request"
                        : "NNTP operation";
                var providerTimeout = new TimeoutException(
                    $"Provider {provider.Host} did not answer the {operationDescription} within " +
                    $"{attemptTimeout.TotalSeconds:0.#} seconds.", e);
                RecordFetch(provider.Id, SegmentFetch.FetchStatus.Timeout,
                    providerStopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= new()).Add((provider.Id, SegmentFetch.FetchStatus.Timeout));
                lastException = ExceptionDispatchInfo.Capture(providerTimeout);
                Log.Debug(providerTimeout,
                    "{Operation} received no response from NNTP provider {Provider}; " +
                    "discarding that connection and trying another provider.",
                    operationDescription,
                    provider.Host);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                providerStopwatch.Stop();
                var reason = ClassifyException(e);
                if (reason == SegmentFetch.FetchStatus.Missing)
                {
                    if (phaseSplitFallback)
                        prepFallback!.MarkResponsive(provider);
                }
                else
                {
                    providerCheckIncomplete = true;
                    if (phaseSplitFallback && !prepFallback!.MarkUnavailable(provider))
                    {
                        lastException = ExceptionDispatchInfo.Capture(e);
                        continue;
                    }
                }
                RecordFetch(provider.Id, reason, providerStopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= new()).Add((provider.Id, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            finally
            {
                hostFailureRegistration.Dispose();
                if (!attemptLifetimeTransferred) attemptCts.Dispose();
                fallbackAdmission?.Dispose();
            }

        }

        cancellationToken.ThrowIfCancellationRequested();
        if (providerCheckIncomplete || attemptedProviders < orderedProviders.Count)
        {
            var cause = lastException?.SourceException ?? new TimeoutException(
                "One or more provider checks did not complete.");
            throw new CouldNotConnectToUsenetException(
                "Article availability remains unverified because one or more " +
                "provider checks returned no result.",
                cause);
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private static async Task VerifyArticleExistsAsync(
        INntpClient provider,
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        var response = await provider.StatAsync(segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (!response.ArticleExists)
            throw new UsenetArticleNotFoundException(segmentId);
    }

    private void RecordFetch(string host, SegmentFetch.FetchStatus status, long durationMs, int retries)
    {
        usageTracker.RecordPrepAttempt(host, status, durationMs);
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
        IDisposable? accompanyingLifetime,
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
                    Stream = new LifetimeYencStream(
                        body.Stream,
                        CombineLifetimes(attemptCts, accompanyingLifetime)),
                });
            case UsenetDecodedArticleResponse article when article.Stream is not null:
                attemptCts.CancelAfter(Timeout.InfiniteTimeSpan);
                lifetimeTransferred = true;
                return (T)(object)(article with
                {
                    Stream = new LifetimeYencStream(
                        article.Stream,
                        CombineLifetimes(attemptCts, accompanyingLifetime)),
                });
            default:
                return result;
        }
    }

    private static IDisposable CombineLifetimes(
        ContextualCancellationTokenSource attemptCts,
        IDisposable? accompanyingLifetime) =>
        accompanyingLifetime is null
            ? attemptCts
            : new ScopeReleaser(() =>
            {
                try
                {
                    attemptCts.Dispose();
                }
                finally
                {
                    accompanyingLifetime.Dispose();
                }
            });

    private static SegmentFetch.FetchStatus ClassifyException(Exception ex)
    {
        if (ex.TryGetCausingException(out UsenetArticleNotFoundException _))
            return SegmentFetch.FetchStatus.Missing;
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
                // Once a provider has failed qualification or crossed the
                // operation-scoped failure threshold, keep ordinary lanes off
                // it while any alternative remains.  Previously the preferred
                // branch filtered quarantine, but an empty preferred set fell
                // through to the generic spread below and immediately selected
                // the same provider again.
                var nonQuarantined = pool
                    .Where(x => !plan.IsQuarantined(x))
                    .ToList();
                var routablePool = nonQuarantined.Count > 0 ? nonQuarantined : pool;
                var preferredCandidates = routablePool
                    .Where(IsPrimaryStatProvider)
                    .Where(plan.IsPreferred)
                    .ToList();
                var preferred = PreferLiveStatProviders(preferredCandidates)
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
                    // provider. Quarantined providers get no lane traffic at
                    // all: their silence resolves through the coordinated
                    // recovery pass instead of burning attempt timeouts in
                    // every batch chain.
                    var preferredFallback = preferred
                        .Skip(1)
                        .OrderByDescending(plan.Coverage)
                        .ThenBy(plan.ProbeRank)
                        .ThenBy(EffectivePriority);
                    var plannedFallback = routablePool
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

                pool = routablePool;
            }

            // STAT transfers no article bodies, so spread health-eligible traffic by
            // normalized occupancy. Pending reservations prevent a burst of lanes
            // from all selecting the same provider. Prefer capacity that has already
            // established a usable socket; cold providers remain in the fallback
            // chain and rejoin primary selection as soon as prewarm or a probe
            // publishes a connection. BackupOnly providers remain rescue-only.
            var primaryCandidates = SelectPrimaryStatTier(pool).ToList();
            var primary = PreferLiveStatProviders(primaryCandidates)
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
        var capacity = Math.Max(1, provider.LiveConnections);
        var committed = StatCommittedDemand(provider);
        return committed / (double)capacity;
    }

    private static int StatCommittedDemand(MultiConnectionNntpClient provider)
    {
        // Acquisitions already queued on the pool gate are real congestion.
        // Without them, every lane sees only the borrowed sockets and herds
        // onto whichever provider probed fastest, queuing seconds of wait
        // behind a pool whose live count looks small and harmless.
        return provider.ActiveConnections +
               provider.PendingSelections +
               provider.PendingAcquisitions;
    }

    private static IReadOnlyList<MultiConnectionNntpClient> PreferLiveStatProviders(
        IReadOnlyList<MultiConnectionNntpClient> candidates)
    {
        var live = candidates.Where(provider => provider.LiveConnections > 0).ToList();
        return live.Count > 0 ? live : candidates;
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
        var responsive = probes
            .Where(x => x.Success && IsPrimaryStatProvider(x.Provider))
            .ToList();
        if (responsive.Count == 0) return [];

        var withCoverage = responsive.Where(x => x.Found > 0).ToList();
        if (withCoverage.Count == 0)
        {
            // Every responsive provider completed its probe with zero coverage —
            // the sampled articles are likely absent on these backbones, but the
            // providers themselves are healthy. They must still carry the bulk
            // workload; otherwise routing falls back to "everyone", including
            // providers whose probes just timed out, and each batch burns a full
            // attempt timeout on them.
            return responsive.Select(x => x.Provider).ToHashSet();
        }

        var bestCoverage = withCoverage.Max(x => x.Found);

        return withCoverage
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

    private List<MultiConnectionNntpClient> SelectStatEligibleProviders(
        CancellationToken cancellationToken)
    {
        lock (_selectLock)
        {
            var priority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority
                           ?? SemaphorePriority.Low;
            return providers
                .Where(x => x.ProviderType != ProviderType.Disabled)
                .Where(x => priority != SemaphorePriority.High || !x.PrepOnly ||
                            x.ProviderType == ProviderType.HealthChecksOnly)
                .Where(x => !IsOverLimit(x))
                .ToList();
        }
    }

    /// <summary>
    /// Operation-scoped verdict bookkeeping for one pipelined health check.
    /// A segment may be reported as confirmed missing only when every provider
    /// in the eligible snapshot answered "missing" for it. Tripped providers
    /// stay in the snapshot: their silence makes segments indeterminate rather
    /// than missing, and the coordinated recovery pass gives them one bounded
    /// chance to answer.
    /// </summary>
    internal sealed class StatVerdictCollector
    {
        // long bitmask: at most 63 addressable providers. Any provider beyond
        // that never gets a bit, which can only widen indeterminate results,
        // never fabricate a missing verdict.
        private const int MaxAddressableProviders = 63;
        private readonly Dictionary<MultiConnectionNntpClient, int> _bits;
        private readonly long _fullMask;
        private readonly ConcurrentDictionary<string, long> _missingMasks =
            new(StringComparer.Ordinal);

        public StatVerdictCollector(
            object owner,
            IReadOnlyList<MultiConnectionNntpClient> eligibleProviders)
        {
            Owner = owner;
            if (eligibleProviders.Count > MaxAddressableProviders)
                Log.Warning(
                    "More than {Max} STAT-eligible providers; missing verdicts are disabled " +
                    "for the overflow and their segments resolve as indeterminate.",
                    MaxAddressableProviders);
            _bits = eligibleProviders
                .Take(MaxAddressableProviders)
                .Select((provider, index) => (provider, index))
                .ToDictionary(x => x.provider, x => x.index);
            _fullMask = _bits.Count == 0 ? 0 : (1L << _bits.Count) - 1;
        }

        public object Owner { get; }
        public IReadOnlyCollection<MultiConnectionNntpClient> EligibleProviders => _bits.Keys;

        public void RecordMissing(string segmentId, MultiConnectionNntpClient provider)
        {
            if (!_bits.TryGetValue(provider, out var bit)) return;
            _missingMasks.AddOrUpdate(segmentId, 1L << bit, (_, mask) => mask | (1L << bit));
        }

        public void RecordFound(string segmentId) =>
            _missingMasks.TryRemove(segmentId, out _);

        public bool HasFullCoverage(string segmentId) =>
            _fullMask != 0 &&
            _missingMasks.GetValueOrDefault(segmentId) == _fullMask;

        public bool HasAnsweredMissing(MultiConnectionNntpClient provider, string segmentId) =>
            _bits.TryGetValue(provider, out var bit) &&
            (_missingMasks.GetValueOrDefault(segmentId) & (1L << bit)) != 0;
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
        catch (UsenetArticleUnverifiableException)
        {
            // Provider unavailability, not article absence: no article is
            // counted missing.
            usageTracker.CompleteHealthCheck(null, 0);
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
        // One verdict collector per operation: a segment may only be confirmed
        // missing when every provider in this snapshot answered "missing".
        var previousCollector = VerdictCollectorContext.Value;
        VerdictCollectorContext.Value = new StatVerdictCollector(
            this, SelectStatEligibleProviders(cancellationToken));
        try
        {
            await CheckAllSegmentsPipelinedWithCollectorAsync(
                segmentIds, depth, fallbackConcurrency, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            VerdictCollectorContext.Value = previousCollector;
        }
    }

    private async Task CheckAllSegmentsPipelinedWithCollectorAsync(
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
            var laneAdmission = qualification.Plan.GetLaneAdmission(fallbackConcurrency);
            Log.Information(
                "health-stat lane-admission requested={Requested} initial={Initial} " +
                "preferredLive={PreferredLive} growthHeadroom={GrowthHeadroom}",
                fallbackConcurrency,
                laneAdmission.Target,
                laneAdmission.PreferredLive,
                HealthLaneGrowthHeadroom);
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

    protected override int GetPipelinedStatLaneTarget(int requestedLanes)
    {
        var plan = BulkStatPlanContext.Value;
        if (plan is null || !ReferenceEquals(plan.Owner, this))
            return requestedLanes;

        return plan.GetLaneAdmission(requestedLanes).Target;
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
            .Select(provider => ProbeStatProviderWithRetryAsync(
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

    private async Task<BulkStatProbe> ProbeStatProviderWithRetryAsync(
        MultiConnectionNntpClient provider,
        IReadOnlyList<string> segmentIds,
        int fallbackDepth,
        bool recoveryProbe,
        CancellationToken cancellationToken)
    {
        var hadEstablishedIdleConnection = provider.IdleConnections > 0;
        var first = await ProbeStatProviderOnceAsync(
                provider, segmentIds, fallbackDepth, recoveryProbe, cancellationToken)
            .ConfigureAwait(false);
        if (first.Success || first.Received > 0 || !hadEstablishedIdleConnection)
            return first;

        // A qualification batch uses one socket. When an established socket
        // returns nothing, cancellation replaces it; retrying once prevents a
        // single stale or wedged idle connection from quarantining an otherwise
        // healthy provider. Do not double the cold-start wait for a zero-live
        // provider: it can still join via a successful late probe or recovery.
        Log.Debug(
            "health-stat probe retry provider={Provider} reason={Reason} received=0",
            provider.Host, first.Status);
        var retry = await ProbeStatProviderOnceAsync(
                provider, segmentIds, fallbackDepth, recoveryProbe, cancellationToken)
            .ConfigureAwait(false);
        return retry with
        {
            Status = retry.Success
                ? "ok-after-fresh-socket-retry"
                : $"{retry.Status}-after-fresh-socket-retry",
        };
    }

    private async Task<BulkStatProbe> ProbeStatProviderOnceAsync(
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
        probeCts.CancelAfter(BulkProbeTimeout);
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
            var collector = VerdictCollectorContext.Value;
            if (collector is not null && !ReferenceEquals(collector.Owner, this)) collector = null;
            var results = await ResolvePipelinedStatBatchAsync(
                chunk, orderedProviders, depth, plan, collector, cancellationToken).ConfigureAwait(false);
            foreach (var result in results)
                yield return result;

            offset += chunk.Count;
        }
    }

    private async Task<IReadOnlyList<PipelinedStatResult>> ResolvePipelinedStatBatchAsync(
        IReadOnlyList<string> segmentIds,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        int fallbackDepth,
        BulkStatPlan? plan,
        StatVerdictCollector? collector,
        CancellationToken cancellationToken)
    {
        var resolved = new PipelinedStatResult?[segmentIds.Count];
        var state = await RunPipelinedStatProviderPassAsync(
            segmentIds, orderedProviders, fallbackDepth, plan, collector, resolved,
            Enumerable.Range(0, segmentIds.Count).ToList(),
            cancellationToken).ConfigureAwait(false);

        // A pass with partial evidence (at least one provider completed an
        // attempt) never aborts the batch: whatever is unanswered materializes
        // as indeterminate and resolves through the operation-wide coordinated
        // recovery pass. The in-place retry below exists for a total outage —
        // no provider completed anything — where a bounded retry beats
        // crawling the whole workload against dead providers.
        if (state.Pending.Count > 0 && state.LastAttemptFailed && !state.AnyCompletedAttempt)
        {
            // Recovery is gated globally so that many simultaneously failing
            // lanes cannot multiply into a provider-wide retry and connection
            // storm.
            var recoveryAdmitted = await _statRecoveryGate
                .WaitAsync(StatRecoveryAdmissionTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!recoveryAdmitted)
                throw BuildUnverifiableBatchException(segmentIds, orderedProviders, state);

            try
            {
                Log.Warning(
                    "Pipelined STAT batch exhausted every provider; retrying {Count} " +
                    "unresolved segments in a coordinated recovery pass.",
                    state.Pending.Count);
                state = await RunPipelinedStatProviderPassAsync(
                    segmentIds, orderedProviders, fallbackDepth, plan, collector, resolved,
                    state.Pending, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _statRecoveryGate.Release();
            }

            if (state.Pending.Count > 0 && state.LastAttemptFailed && !state.AnyCompletedAttempt)
                throw BuildUnverifiableBatchException(segmentIds, orderedProviders, state);
        }

        for (var i = 0; i < resolved.Length; i++)
        {
            var result = resolved[i];
            if (result is { Exists: true })
            {
                collector?.RecordFound(result.SegmentId);
                continue;
            }

            // Nobody found the segment. It is confirmed missing only when every
            // provider in the operation's eligible snapshot answered "missing";
            // any silent provider (quarantined, timed out, gate-busy backup)
            // makes the verdict indeterminate. With no collector in scope there
            // is no snapshot to judge against, so legacy semantics apply.
            var indeterminate = collector is not null &&
                                !collector.HasFullCoverage(segmentIds[i]);
            resolved[i] = new PipelinedStatResult
            {
                SegmentId = segmentIds[i],
                Exists = false,
                Indeterminate = indeterminate,
            };
        }

        return resolved!;
    }

    private static UsenetArticleUnverifiableException BuildUnverifiableBatchException(
        IReadOnlyList<string> segmentIds,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        PipelinedStatPassState state)
    {
        // Both the primary walk and the gated recovery pass ended in provider
        // errors. Abort loudly, but classify it as provider unavailability:
        // nothing here is evidence that any article is missing.
        return new UsenetArticleUnverifiableException(
            state.Pending.Select(index => segmentIds[index]).ToArray(),
            orderedProviders.Select(provider => provider.Host).Distinct().ToArray(),
            state.LastException?.SourceException);
    }

    /// <summary>
    /// Operation-wide coordinated recovery: runs once after the bulk lanes
    /// drain, under a single bounded budget, and consults exactly the providers
    /// whose silence left segments indeterminate — quarantined providers,
    /// tripped providers (via a breaker recovery probe), and BackupOnly
    /// providers through the recovery gate. A provider that answers is
    /// re-admitted to the plan. Segments answered "missing" by every eligible
    /// provider become confirmed missing; anything else is unverifiable.
    /// </summary>
    protected override async Task ResolveIndeterminateSegmentsAsync(
        IReadOnlyList<string> segmentIds,
        int depth,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var collector = VerdictCollectorContext.Value;
        if (collector is null || !ReferenceEquals(collector.Owner, this))
            throw new UsenetArticleUnverifiableException(segmentIds, []);

        var plan = BulkStatPlanContext.Value;
        if (plan is not null && !ReferenceEquals(plan.Owner, this)) plan = null;

        var candidates = collector.EligibleProviders
            .Where(provider => !IsOverLimit(provider))
            .Select(provider => (
                Provider: provider,
                Targets: segmentIds
                    .Where(segmentId => !collector.HasAnsweredMissing(provider, segmentId))
                    .ToArray()))
            .Where(candidate => candidate.Targets.Length > 0)
            .ToList();

        var timer = Stopwatch.StartNew();
        Log.Information(
            "health-stat recovery start segments={Segments} providers={Providers}",
            segmentIds.Count,
            string.Join(',', candidates.Select(candidate => candidate.Provider.Host)));
        usageTracker.ReportRecoveryNotice(
            new QueueRecoveryNotice("health", segmentIds.Count));

        var foundSegments = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var unavailableProviders = new ConcurrentDictionary<string, byte>();
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(IndeterminateRecoveryBudget);

        await Task.WhenAll(candidates.Select(async candidate =>
        {
            var (provider, targets) = candidate;
            IDisposable? lease = null;
            var received = 0;
            var found = 0;
            var missing = 0;
            var recoveryConnections = 0;
            var attemptFailed = false;
            var callerCancelled = false;
            Stopwatch? attemptTimer = null;
            try
            {
                if (provider.ProviderType == ProviderType.BackupOnly)
                    lease = await _backupRecovery.EnterAsync(provider, budgetCts.Token)
                        .ConfigureAwait(false);

                var providerDepth = ResolveHealthDepth(provider, depth);
                var recoveryBatches = PartitionRecoveryTargets(
                    targets, ResolveRecoveryConcurrency(provider, targets.Length));
                recoveryConnections = recoveryBatches.Count;
                attemptTimer = Stopwatch.StartNew();
                await Task.WhenAll(recoveryBatches.Select(async batch =>
                {
                    var results = provider.IsTripped
                        ? provider.StatsPipelinedRecoveryProbeAsync(
                            batch, providerDepth, budgetCts.Token)
                        : provider.StatsPipelinedAsync(
                            batch, providerDepth, budgetCts.Token);
                    var batchReceived = 0;
                    await foreach (var result in results
                                       .WithCancellation(budgetCts.Token).ConfigureAwait(false))
                    {
                        if (batchReceived >= batch.Length ||
                            !string.Equals(
                                result.SegmentId, batch[batchReceived], StringComparison.Ordinal))
                            throw new InvalidDataException(
                                $"Provider {provider.Host} returned an invalid recovery STAT response.");

                        if (result.Exists)
                        {
                            foundSegments.TryAdd(result.SegmentId, 0);
                            Interlocked.Increment(ref found);
                        }
                        else
                        {
                            collector.RecordMissing(result.SegmentId, provider);
                            Interlocked.Increment(ref missing);
                        }
                        batchReceived++;
                        Interlocked.Increment(ref received);
                    }

                    if (batchReceived != batch.Length)
                        throw new IOException(
                            $"Provider {provider.Host} ended a recovery STAT pass after " +
                            $"{batchReceived} of {batch.Length} responses.");
                })).ConfigureAwait(false);

                plan?.Requalify(provider);
                Log.Debug(
                    "health-stat recovery provider={Provider} status=ok targets={Targets} " +
                    "received={Received} connections={Connections}",
                    provider.Host, targets.Length, received, recoveryBatches.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                callerCancelled = true;
                throw;
            }
            catch (OperationCanceledException)
            {
                attemptFailed = true;
                unavailableProviders.TryAdd(provider.Host, 0);
                Log.Debug(
                    "health-stat recovery provider={Provider} status=budget-expired " +
                    "targets={Targets} received={Received} connections={Connections}",
                    provider.Host, targets.Length, received, recoveryConnections);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                attemptFailed = true;
                unavailableProviders.TryAdd(provider.Host, 0);
                Log.Debug(e,
                    "health-stat recovery provider={Provider} status=failed targets={Targets} " +
                    "received={Received} connections={Connections}",
                    provider.Host, targets.Length, received, recoveryConnections);
            }
            finally
            {
                lease?.Dispose();
                // The bulk provider table must include the operation-wide
                // recovery pass. Without this, a provider that timed out in a
                // lane and then found the article during recovery was displayed
                // as Found=0 with one failed batch even though it completed the
                // health check. Gate waits are deliberately excluded: no NNTP
                // provider attempt began until the command timer was created.
                if (!callerCancelled && attemptTimer is not null)
                    plan?.RecordAttempt(
                        provider, targets.Length, received, found, missing,
                        attemptTimer.ElapsedMilliseconds, attemptFailed);
            }
        })).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        string? confirmedMissing = null;
        var stillUnresolved = new List<string>();
        foreach (var segmentId in segmentIds)
        {
            if (foundSegments.ContainsKey(segmentId))
            {
                collector.RecordFound(segmentId);
                continue;
            }

            if (collector.HasFullCoverage(segmentId))
            {
                confirmedMissing = segmentId;
                break;
            }

            stillUnresolved.Add(segmentId);
        }

        Log.Information(
            "health-stat recovery done segments={Segments} found={Found} " +
            "confirmedMissing={ConfirmedMissing} unresolved={Unresolved} " +
            "unavailable={Unavailable} ms={ElapsedMs}",
            segmentIds.Count, foundSegments.Count, confirmedMissing is not null,
            stillUnresolved.Count,
            string.Join(',', unavailableProviders.Keys), timer.ElapsedMilliseconds);

        if (confirmedMissing is not null)
        {
            usageTracker.ReportRecoveryNotice(null);
            throw new UsenetArticleNotFoundException(confirmedMissing);
        }
        if (stillUnresolved.Count > 0)
        {
            usageTracker.ReportRecoveryNotice(null);
            throw new UsenetArticleUnverifiableException(
                stillUnresolved,
                unavailableProviders.Keys.OrderBy(host => host).ToArray());
        }
        usageTracker.ReportRecoveryNotice(null);
    }

    private static int ResolveRecoveryConcurrency(
        MultiConnectionNntpClient provider,
        int targetCount)
    {
        if (targetCount <= 1 || provider.IsTripped ||
            provider.ProviderType == ProviderType.BackupOnly)
            return 1;

        var usefulConnections = (targetCount + HealthRecoverySegmentsPerConnection - 1) /
                                HealthRecoverySegmentsPerConnection;
        var establishedIdle = Math.Max(1, provider.IdleConnections);
        return Math.Clamp(
            Math.Min(usefulConnections, establishedIdle),
            1,
            HealthRecoveryMaxConnectionsPerProvider);
    }

    internal static IReadOnlyList<string[]> PartitionRecoveryTargets(
        IReadOnlyList<string> targets,
        int requestedPartitions)
    {
        if (targets.Count == 0) return [];
        var partitions = Math.Clamp(requestedPartitions, 1, targets.Count);
        var result = new string[partitions][];
        var offset = 0;
        for (var index = 0; index < partitions; index++)
        {
            var remaining = targets.Count - offset;
            var remainingPartitions = partitions - index;
            var count = (remaining + remainingPartitions - 1) / remainingPartitions;
            result[index] = targets.Skip(offset).Take(count).ToArray();
            offset += count;
        }

        return result;
    }

    private readonly record struct PipelinedStatPassState(
        List<int> Pending,
        bool LastAttemptFailed,
        bool AnyCompletedAttempt,
        ExceptionDispatchInfo? LastException);

    private async Task<PipelinedStatPassState> RunPipelinedStatProviderPassAsync(
        IReadOnlyList<string> segmentIds,
        IReadOnlyList<MultiConnectionNntpClient> orderedProviders,
        int fallbackDepth,
        BulkStatPlan? plan,
        StatVerdictCollector? collector,
        PipelinedStatResult?[] resolved,
        List<int> pending,
        CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? lastException = null;
        var lastAttemptFailed = false;
        var anyCompletedAttempt = false;
        var operationStopwatch = Stopwatch.StartNew();

        for (var providerIndex = 0; providerIndex < orderedProviders.Count; providerIndex++)
        {
            if (pending.Count == 0) break;
            cancellationToken.ThrowIfCancellationRequested();
            var remainingOperationTime = ProviderOperationTimeout - operationStopwatch.Elapsed;
            if (remainingOperationTime <= TimeSpan.Zero)
            {
                lastAttemptFailed = true;
                lastException = ExceptionDispatchInfo.Capture(new TimeoutException(
                    "No Usenet provider completed the pipelined STAT batch within " +
                    $"{ProviderOperationTimeout.TotalSeconds:0.#} seconds."));
                break;
            }

            var provider = orderedProviders[providerIndex];
            var isLastProvider = providerIndex == orderedProviders.Count - 1;
            var commandTimeout = isLastProvider
                ? ProviderOperationTimeout
                : ProviderAttemptTimeout;
            // The attempt is split into two phases owned by the provider client:
            // waiting for a usable connection, and the command window that
            // starts only after a connection is acquired. A late handshake
            // therefore never leaves a freshly authenticated socket with a
            // near-zero command window. A non-final provider only gets a short
            // acquisition wait: when its pool is congested, the batch moves on
            // to a provider with free capacity instead of camping on the queue.
            var acquisitionTimeout = isLastProvider
                ? remainingOperationTime
                : ProviderAttemptTimeout < remainingOperationTime
                    ? ProviderAttemptTimeout
                    : remainingOperationTime;
            using var attemptCts =
                ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.SetContext(new ProviderAttemptContext(
                acquisitionTimeout,
                commandTimeout,
                ResponseInactivityTimeout: PipelinedStatResponseInactivityTimeout));
            var attempted = pending;
            var batch = attempted.Select(index => segmentIds[index]).ToArray();
            var stillPending = new List<int>(attempted.Count);
            var received = 0;
            var found = 0;
            var missing = 0;
            lastAttemptFailed = false;
            var stopwatch = Stopwatch.StartNew();
            IDisposable? recoveryLease = null;
            var gateSkipped = false;
            try
            {
                if (provider.ProviderType == ProviderType.BackupOnly)
                {
                    // Health lanes use BackupOnly immediately only when an
                    // authenticated idle socket is actually available and has
                    // not already been claimed by another recovery lane. Cold
                    // backups go directly to the single coordinated recovery
                    // pass, which owns reserved creation capacity, instead of
                    // first burning the ordinary five-second acquisition wait.
                    // Calls outside a health-verdict scope retain the legacy
                    // gate behavior because they have no coordinated pass.
                    recoveryLease = collector is null
                        ? _backupRecovery.TryEnter(provider)
                        : _backupRecovery.TryEnterEstablished(provider);
                    if (recoveryLease is null)
                    {
                        gateSkipped = true;
                        Log.Debug(
                            "Backup provider {Provider} has no unclaimed authenticated idle " +
                            "connection or recovery slots are busy; deferring {Count} " +
                            "pipelined STAT segments to coordinated recovery.",
                            provider.Host, attempted.Count);
                        continue;
                    }
                }

                var providerDepth = ResolveHealthDepth(provider, fallbackDepth);
                await foreach (var result in provider.StatsPipelinedAsync(
                                       batch, providerDepth, attemptCts.Token)
                                   .WithCancellation(attemptCts.Token).ConfigureAwait(false))
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
                        collector?.RecordMissing(expectedSegmentId, provider);
                    }
                    received++;
                }

                if (received != attempted.Count)
                    throw new IOException(
                        $"Provider {provider.Host} ended a pipelined STAT batch after {received} of {attempted.Count} responses.");
                anyCompletedAttempt = true;
            }
            catch (OperationCanceledException e) when (
                !cancellationToken.IsCancellationRequested && attemptCts.Token.IsCancellationRequested)
            {
                lastAttemptFailed = true;
                var providerTimeout = new TimeoutException(
                    $"Provider {provider.Host} did not complete the pipelined STAT batch in time.", e);
                lastException = ExceptionDispatchInfo.Capture(providerTimeout);
                for (var i = received; i < attempted.Count; i++)
                    stillPending.Add(attempted[i]);
                Log.Debug(providerTimeout,
                    "Pipelined STAT batch timed out on provider {Provider}; " +
                    "retrying {Count} unresolved segments as a batch.",
                    provider.Host, stillPending.Count);
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
                recoveryLease?.Dispose();
                if (!gateSkipped)
                    plan?.RecordAttempt(provider, attempted.Count, received, found, missing,
                        stopwatch.ElapsedMilliseconds, lastAttemptFailed);
            }

            pending = stillPending;
        }

        return new PipelinedStatPassState(pending, lastAttemptFailed, anyCompletedAttempt, lastException);
    }

    private sealed class BackupRecoveryCoordinator(int maxConnections)
    {
        private readonly SemaphoreSlim _slots = new(maxConnections, maxConnections);
        private readonly object _lock = new();
        private readonly Dictionary<MultiConnectionNntpClient, int> _active = [];

        public async ValueTask<IDisposable> EnterAsync(
            MultiConnectionNntpClient provider,
            CancellationToken cancellationToken)
        {
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
                _active[provider] = _active.GetValueOrDefault(provider) + 1;
            return new Lease(this, provider);
        }

        /// <summary>
        /// Non-blocking entry for lane traffic: acquire a slot only if one is
        /// free right now, never queue. Returns null when all slots are busy.
        /// </summary>
        public IDisposable? TryEnter(MultiConnectionNntpClient provider)
        {
            if (!_slots.Wait(0)) return null;
            lock (_lock)
                _active[provider] = _active.GetValueOrDefault(provider) + 1;
            return new Lease(this, provider);
        }

        /// <summary>
        /// Non-blocking entry for health-lane fallback. Each admitted lane must
        /// have a distinct authenticated idle socket available now; cold
        /// providers and excess lanes are left for coordinated recovery.
        /// </summary>
        public IDisposable? TryEnterEstablished(MultiConnectionNntpClient provider)
        {
            lock (_lock)
            {
                var active = _active.GetValueOrDefault(provider);
                if (active >= provider.IdleConnections || !_slots.Wait(0)) return null;
                _active[provider] = active + 1;
                return new Lease(this, provider);
            }
        }

        private void Exit(MultiConnectionNntpClient provider)
        {
            var closeIdle = false;
            lock (_lock)
            {
                var remaining = _active.GetValueOrDefault(provider) - 1;
                if (remaining <= 0)
                {
                    _active.Remove(provider);
                    closeIdle = true;
                }
                else
                    _active[provider] = remaining;
            }

            try
            {
                if (closeIdle) provider.CloseIdleConnections();
            }
            finally
            {
                _slots.Release();
            }
        }

        private sealed class Lease(
            BackupRecoveryCoordinator owner,
            MultiConnectionNntpClient provider) : IDisposable
        {
            private BackupRecoveryCoordinator? _owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Exit(provider);
            }
        }
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

    private sealed record HealthPrimeProbe(
        MultiConnectionNntpClient Provider,
        int Received,
        int Found,
        bool Success);

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
        private readonly HashSet<MultiConnectionNntpClient> _quarantined;
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
            // A provider whose qualification probe failed or timed out gets no
            // lane traffic. Its silence keeps segments indeterminate; the
            // coordinated recovery pass gives it one bounded chance to answer,
            // and a successful late probe or recovery re-admits it.
            _quarantined = probes
                .Where(x => !x.Success)
                .Select(x => x.Provider)
                .ToHashSet();
            var ranked = probes
                .OrderByDescending(x => x.Success)
                .ThenByDescending(x => x.Found)
                .ThenByDescending(x => x.Rate)
                .ThenBy(x => x.Provider.Priority)
                .Select((probe, index) => new { probe.Provider, Rank = index });
            _probeRanks = ranked.ToDictionary(x => x.Provider, x => x.Rank);
        }

        public bool IsQuarantined(MultiConnectionNntpClient provider)
        {
            lock (_preferred) return _quarantined.Contains(provider);
        }

        public void Quarantine(MultiConnectionNntpClient provider)
        {
            bool added;
            lock (_preferred) added = _quarantined.Add(provider);
            if (added)
            {
                Log.Information(
                    "health-stat quarantine provider={Provider} reason=bulk-attempt-failures",
                    provider.Host);
                Volatile.Read(ref _connectionAllocation)?.Reconcile();
            }
        }

        public void Requalify(MultiConnectionNntpClient provider)
        {
            bool removed;
            lock (_preferred) removed = _quarantined.Remove(provider);
            if (removed)
            {
                Log.Information(
                    "health-stat requalify provider={Provider}", provider.Host);
                Volatile.Read(ref _connectionAllocation)?.Reconcile();
            }
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
                if (probe.Success) _quarantined.Remove(probe.Provider);
                else _quarantined.Add(probe.Provider);
            }
            Volatile.Read(ref _connectionAllocation)?.Reconcile();
        }

        public (MultiConnectionNntpClient[] Probed, HashSet<MultiConnectionNntpClient> Preferred)
            SnapshotAllocationState()
        {
            var probed = _probes.Keys.ToArray();
            lock (_preferred)
                return (
                    probed,
                    _preferred
                        .Where(provider => !_quarantined.Contains(provider))
                        .ToHashSet());
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

        public (int Target, int PreferredLive) GetLaneAdmission(int requestedLanes)
        {
            int preferredLive;
            lock (_preferred)
                preferredLive = _preferred
                    .Where(provider => !_quarantined.Contains(provider))
                    .Sum(provider => provider.LiveConnections);

            var target = Math.Clamp(
                preferredLive + HealthLaneGrowthHeadroom,
                1,
                Math.Max(1, requestedLanes));
            return (target, preferredLive);
        }

        public double SelectionScore(MultiConnectionNntpClient provider)
        {
            var capacity = Math.Max(1, provider.LiveConnections);
            var committed = StatCommittedDemand(provider);
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
            var stats = _attempts.GetOrAdd(provider, static _ => new BulkStatAttemptStats());
            stats.Record(attempted, received, found, missing, elapsedMs, failed);

            // Concurrent lanes complete out of order, so "consecutive" failures
            // are not a stable signal: one older success can otherwise erase a
            // timeout storm and immediately re-admit the provider. Two failed
            // attempts quarantine it for the rest of this bulk plan. A successful
            // late probe or coordinated recovery may still re-admit it explicitly,
            // and the next health operation starts with a fresh plan.
            if (failed)
            {
                if (stats.FailureCount >= QuarantineConsecutiveFailureThreshold)
                    Quarantine(provider);
            }
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
        private IDisposable? _recoveryReservation;
        private int _reservedRecoveryConnections;
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

                EnsureRecoveryReserve(probed, preferred);

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
                    Math.Min(
                        preferred.Sum(provider => provider.MaxConnections),
                        Math.Max(1,
                            owner.ApplicationConnectionLimit - _reservedRecoveryConnections)));
                var preferredLive = preferred.Sum(provider => provider.LiveConnections);
                var totalLive = owner.TotalLiveConnections;
                var globalHeadroom = Math.Max(
                    0,
                    owner.ApplicationConnectionLimit - totalLive - _reservedRecoveryConnections);
                var reclaimNeeded = Math.Max(0, usefulTarget - preferredLive - globalHeadroom);

                foreach (var provider in probed
                             .Where(provider => !preferred.Contains(provider))
                             .OrderByDescending(provider => provider.IdleConnections))
                {
                    if (_suspensions.ContainsKey(provider)) continue;
                    try
                    {
                        // A provider quarantined after a zero-response probe is
                        // intentionally kept out of primary lanes, but its final
                        // recovery pass must not be forced through one socket.
                        // Healthy zero-coverage providers retain the smaller
                        // reclamation floor so normal lane/backup capacity is
                        // unaffected.
                        var retainedFloor = plan.IsQuarantined(provider)
                            ? HealthQuarantinedRecoveryIdleFloor
                            : HealthReclamationIdleFloor;
                        var reclaimable = Math.Max(
                            0, provider.IdleConnections - retainedFloor);
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

        private void EnsureRecoveryReserve(
            IReadOnlyCollection<MultiConnectionNntpClient> probed,
            IReadOnlySet<MultiConnectionNntpClient> preferred)
        {
            var budget = owner.ConnectionBudget;
            if (_recoveryReservation is not null || budget is null) return;

            var reserveTarget = Math.Min(
                HealthRecoveryConnectionReserve,
                Math.Min(
                    Math.Max(0, owner.ApplicationConnectionLimit - 1),
                    owner.Providers
                        .Where(provider => provider.ProviderType == ProviderType.BackupOnly)
                        .Where(provider => !provider.IsTripped)
                        .Where(provider => !owner.IsOverLimit(provider))
                        .Sum(provider => provider.MaxConnections)));
            if (reserveTarget == 0) return;

            var trimNeeded = Math.Max(
                0, reserveTarget - budget.AvailableStandardCapacity);
            var candidates = probed
                .Where(provider => provider.ProviderType != ProviderType.BackupOnly)
                .OrderBy(provider => preferred.Contains(provider) ? 1 : 0)
                .ThenBy(provider => preferred.Contains(provider)
                    ? plan.Coverage(provider)
                    : 0)
                .ThenByDescending(provider => provider.IdleConnections)
                .ToArray();

            TrimCandidates(HealthReclamationIdleFloor);

            if (!budget.TryReserveStandardCapacity(
                    reserveTarget, out var reservation))
            {
                Log.Warning(
                    "health-stat recovery-reserve action=failed requested={Requested} " +
                    "availableStandard={AvailableStandard}",
                    reserveTarget, budget.AvailableStandardCapacity);
                return;
            }

            _recoveryReservation = reservation;
            _reservedRecoveryConnections = reserveTarget;
            Log.Information(
                "health-stat recovery-reserve action=reserved connections={Connections}",
                reserveTarget);
            return;

            void TrimCandidates(int retainedFloor)
            {
                foreach (var provider in candidates)
                {
                    if (trimNeeded == 0) return;
                    var reclaimable = Math.Max(
                        0, provider.IdleConnections - retainedFloor);
                    var requested = Math.Min(trimNeeded, reclaimable);
                    if (requested == 0) continue;

                    var retained = provider.IdleConnections - requested;
                    var reclaimed = provider.TrimIdleConnections(retained);
                    trimNeeded = Math.Max(0, trimNeeded - reclaimed);
                    Log.Information(
                        "health-stat recovery-reserve provider={Provider} action=trim " +
                        "retained={Retained} reclaimed={Reclaimed} remaining={Remaining}",
                        provider.Host, retained, reclaimed, trimNeeded);
                }
            }
        }

        public void Dispose()
        {
            MultiConnectionNntpClient[] restore;
            IDisposable? recoveryReservation;
            int reclaimed;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                restore = _suspensions.Keys.ToArray();
                foreach (var suspension in _suspensions.Values)
                    suspension.Dispose();
                _suspensions.Clear();
                recoveryReservation = _recoveryReservation;
                _recoveryReservation = null;
                _reservedRecoveryConnections = 0;
                reclaimed = _reclaimed;
            }

            foreach (var provider in owner.Providers
                         .Where(provider => provider.ProviderType == ProviderType.BackupOnly))
                provider.CloseIdleConnections();
            recoveryReservation?.Dispose();

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

        public long FailureCount => Interlocked.Read(ref _failures);

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
            if (failed)
            {
                Interlocked.Increment(ref _failures);
            }
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
