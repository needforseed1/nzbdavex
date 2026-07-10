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
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(
    List<MultiConnectionNntpClient> providers,
    ProviderUsageTracker usageTracker,
    MetricsWriter? metricsWriter = null,
    ProviderBytesTracker? bytesTracker = null,
    Func<bool>? cascadeEnabled = null,
    Func<bool>? prepSpreadEnabled = null
) : NntpClient
{
    private const int BulkStatProbeSize = 32;
    private const int BulkStatProbeThreshold = 256;
    private static readonly TimeSpan BulkStatProbeJoinWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan BulkStatProbeTimeout = TimeSpan.FromSeconds(2);
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
            x => x.StatAsync(segmentId, cancellationToken), cancellationToken, statOperation: true);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync
    (
        SegmentId segmentId,
        CancellationToken cancellationToken
    )
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
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
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
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
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
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
        Func<INntpClient, Task<T>> task,
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
        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await task.Invoke(provider).ConfigureAwait(false);
                stopwatch.Stop();

                // if no article with that message-id is found, try again with the next provider.
                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                {
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                    (priorMisses ??= new()).Add((provider.Host, SegmentFetch.FetchStatus.Missing));
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
                    usageTracker.RecordSuccess(provider.Host);
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Ok, stopwatch.ElapsedMilliseconds, i);
                    if (i > 0)
                    {
                        usageTracker.RecordFailoverSave();
                        RecordFailoverMisses(priorMisses, rescuer: provider.Host);
                    }
                    result = WrapStreamForByteCounting(result, provider.Host);
                }
                else
                {
                    RecordFetch(provider.Host, SegmentFetch.FetchStatus.Missing, stopwatch.ElapsedMilliseconds, i);
                }

                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                stopwatch.Stop();
                var reason = ClassifyException(e);
                RecordFetch(provider.Host, reason, stopwatch.ElapsedMilliseconds, i);
                (priorMisses ??= new()).Add((provider.Host, reason));
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

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
                => (T)(object)(b with { Stream = new CountingYencStream(b.Stream, bytesTracker, host) }),
            UsenetDecodedArticleResponse a
                => (T)(object)(a with { Stream = new CountingYencStream(a.Stream, bytesTracker, host) }),
            _ => result,
        };
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
        out MultiConnectionNntpClient? reserved)
    {
        lock (_selectLock)
        {
            var priority = cancellationToken.GetContext<DownloadPriorityContext>()?.Priority ?? SemaphorePriority.Low;
            var enabled = providers
                .Where(x => x.ProviderType is not (ProviderType.Disabled or ProviderType.HealthChecksOnly))
                .Where(x => priority != SemaphorePriority.High || !x.PrepOnly)
                .Where(x => !IsOverLimit(x))
                .ToList();

            var healthy = enabled.Where(x => !x.IsTripped).ToList();
            var pool = healthy.Count > 0 ? healthy : enabled;

            if (priority != SemaphorePriority.High && prepSpreadEnabled?.Invoke() == true)
            {
                var spreadPool = pool
                    .Where(x => x.ProviderType == ProviderType.Pooled && x.PrepSpreadEnabled)
                    .ToList();
                if (spreadPool.Count > 0)
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
                        .OrderBy(x => x.ProviderType)
                        .ThenBy(EffectivePriority)
                        .ThenByDescending(GetRemainingBytes)
                        .ThenBy(EstimatedDeliveryScore);
                    var spreadOrdered = spread.Concat(fallback).ToList();
                    reserved = spreadOrdered[0];
                    reserved.ReservePending();
                    return spreadOrdered;
                }
            }

            var byTier = pool.OrderBy(x => x.ProviderType);
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
                    var plannedFallback = pool
                        .Where(x => !preferredSet.Contains(x))
                        .OrderBy(plan.ProbeRank)
                        .ThenBy(x => x.ProviderType)
                        .ThenBy(EffectivePriority)
                        .ThenByDescending(GetRemainingBytes);
                    var planned = preferred.Concat(plannedFallback).ToList();
                    reserved = planned[0];
                    reserved.ReservePending();
                    return planned;
                }
            }

            // STAT transfers no article bodies, so spread health-eligible traffic by
            // normalized occupancy. Pending reservations prevent a burst of lanes
            // from all selecting the same nominally-fast provider before its sockets
            // have finished connecting. BackupOnly providers remain rescue-only.
            var primary = pool
                .Where(IsPrimaryStatProvider)
                .OrderBy(PrepSpreadScore)
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

    private double EstimatedDeliveryScore(MultiConnectionNntpClient provider)
    {
        var inFlight = provider.ActiveConnections + provider.PendingSelections + 1;
        var bytesPerMs = bytesTracker?.GetBytesPerMs(provider.Host) ?? 0d;
        return bytesPerMs > 0 ? inFlight / bytesPerMs : inFlight;
    }

    private static double PrepSpreadScore(MultiConnectionNntpClient provider)
    {
        var capacity = Math.Max(1, provider.ActiveConnections + provider.AvailableConnections);
        var committed = provider.ActiveConnections + provider.PendingSelections;
        return committed / (double)capacity;
    }

    private static bool IsPrimaryStatProvider(MultiConnectionNntpClient provider) =>
        provider.ProviderType is ProviderType.Pooled
            or ProviderType.BackupAndStats
            or ProviderType.HealthChecksOnly;

    private bool IsOverLimit(MultiConnectionNntpClient client)
    {
        var limit = client.ByteLimit;
        if (bytesTracker == null || !limit.HasValue || limit.Value <= 0) return false;
        var used = bytesTracker.GetLifetime(client.Host) + client.BytesUsedOffset;
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
        var used = bytesTracker.GetLifetime(client.Host) + client.BytesUsedOffset;
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

    public override async Task CheckAllSegmentsPipelinedAsync(
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
            segmentIds, depth, cancellationToken).ConfigureAwait(false);
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
            var healthy = enabled.Where(x => !x.IsTripped).ToList();
            candidates = healthy.Count > 0 ? healthy : enabled;
        }

        if (candidates.Count <= 1) return BulkStatQualification.None;

        var sampleIndexes = SelectProbeIndexes(segmentIds.Count, BulkStatProbeSize);
        var sample = sampleIndexes.Select(index => segmentIds[index]).ToArray();
        var qualificationTimer = Stopwatch.StartNew();
        var pendingProbes = candidates
            .Select(provider => ProbeStatProviderAsync(provider, sample, depth, cancellationToken))
            .ToList();
        var probes = new List<BulkStatProbe>(pendingProbes.Count);
        var endedEarly = false;

        while (pendingProbes.Count > 0)
        {
            var completedTask = await Task.WhenAny(pendingProbes).ConfigureAwait(false);
            pendingProbes.Remove(completedTask);
            var completedProbe = await completedTask.ConfigureAwait(false);
            probes.Add(completedProbe);

            var hasCompletePrimary = completedProbe.Success &&
                                     completedProbe.Found == sample.Length &&
                                     IsPrimaryStatProvider(completedProbe.Provider);
            if (!hasCompletePrimary) continue;

            // A full-coverage result cannot be beaten. Give similarly fast peers a
            // brief chance to join the plan, then stop waiting on slow or stalled
            // providers and begin the bulk pass.
            if (pendingProbes.Count > 0)
                await Task.Delay(BulkStatProbeJoinWindow, cancellationToken).ConfigureAwait(false);

            var joinedProbes = pendingProbes.Where(x => x.IsCompleted).ToList();
            foreach (var joinedTask in joinedProbes)
            {
                pendingProbes.Remove(joinedTask);
                probes.Add(await joinedTask.ConfigureAwait(false));
            }

            endedEarly = pendingProbes.Count > 0;
            break;
        }

        foreach (var probe in probes)
            LogBulkStatProbe(probe, sample.Length);

        var successful = probes.Where(x => x.Success).ToList();
        if (successful.Count == 0) return BulkStatQualification.None;

        var primaryCandidates = successful
            .Where(x => IsPrimaryStatProvider(x.Provider))
            .ToList();
        var bestCoverage = primaryCandidates.Count == 0 ? 0 : primaryCandidates.Max(x => x.Found);
        var preferred = bestCoverage == 0
            ? []
            : primaryCandidates.Where(x => x.Found == bestCoverage).Select(x => x.Provider).ToHashSet();
        var plan = new BulkStatPlan(this, probes, preferred);
        plan.ObserveLateProbes(pendingProbes, sample.Length, cancellationToken);

        Log.Information(
            "health-stat plan sample={Sample} preferred={Preferred} bestCoverage={BestCoverage}/{Sample} " +
            "qualificationMs={QualificationMs} endedEarly={EndedEarly}",
            sample.Length,
            preferred.Count == 0 ? "default-routing" : string.Join(',', preferred.Select(x => x.Host)),
            bestCoverage, sample.Length, qualificationTimer.ElapsedMilliseconds, endedEarly);

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
            await foreach (var result in provider.StatsPipelinedAsync(segmentIds, providerDepth, probeCts.Token)
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
        var effectiveDepth = ResolveBodyDepth(primary, depth);
        var chunkSize = ResolvePipelinedChunkSize(effectiveDepth);

        for (var offset = 0; offset < segmentIds.Count; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = Slice(segmentIds, offset, chunkSize);
            var nextIndex = 0;
            var rescueFrom = chunk.Count;

            await using (var enumerator = primary
                             .DecodedBodiesPipelinedAsync(chunk, effectiveDepth, cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (nextIndex < chunk.Count)
                {
                    PipelinedBodyResult result;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                        result = enumerator.Current;
                    }
                    catch (Exception e) when (!e.IsCancellationException())
                    {
                        Log.Debug(e, "Pipelined BODY chunk failed on primary provider {Provider}; rescuing remaining segments.",
                            primary.Host);
                        rescueFrom = nextIndex;
                        break;
                    }

                    if (result.Found)
                    {
                        nextIndex++;
                        usageTracker.RecordSuccess(primary.Host);
                        yield return WrapPipelinedBody(result, primary.Host);
                        continue;
                    }

                    rescueFrom = nextIndex;
                    break;
                }

                if (nextIndex < chunk.Count && rescueFrom == chunk.Count)
                    rescueFrom = nextIndex;
            }

            for (var rescueIndex = rescueFrom; rescueIndex < chunk.Count; rescueIndex++)
            {
                var segmentId = chunk[rescueIndex];
                yield return await RescuePipelinedBody(
                    segmentId,
                    new PipelinedBodyResult { SegmentId = segmentId, Found = false, Stream = null },
                    cancellationToken).ConfigureAwait(false);
            }
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
        var effectiveDepth = ResolveBodyDepth(primary, depth);
        var chunkSize = ResolvePipelinedChunkSize(effectiveDepth);

        for (var offset = 0; offset < segmentIds.Count; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = Slice(segmentIds, offset, chunkSize);
            var nextIndex = 0;
            var rescueFrom = chunk.Count;

            await using (var enumerator = primary
                             .DecodedArticlesPipelinedAsync(chunk, effectiveDepth, cancellationToken)
                             .GetAsyncEnumerator(cancellationToken))
            {
                while (nextIndex < chunk.Count)
                {
                    PipelinedArticleResult result;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                        result = enumerator.Current;
                    }
                    catch (Exception e) when (!e.IsCancellationException())
                    {
                        Log.Debug(e, "Pipelined ARTICLE chunk failed on primary provider {Provider}; rescuing remaining segments.",
                            primary.Host);
                        rescueFrom = nextIndex;
                        break;
                    }

                    if (result.Found)
                    {
                        nextIndex++;
                        usageTracker.RecordSuccess(primary.Host);
                        yield return WrapPipelinedArticle(result, primary.Host);
                        continue;
                    }

                    rescueFrom = nextIndex;
                    break;
                }

                if (nextIndex < chunk.Count && rescueFrom == chunk.Count)
                    rescueFrom = nextIndex;
            }

            for (var rescueIndex = rescueFrom; rescueIndex < chunk.Count; rescueIndex++)
            {
                var segmentId = chunk[rescueIndex];
                yield return await RescuePipelinedArticle(
                    segmentId,
                    new PipelinedArticleResult { SegmentId = segmentId, Found = false },
                    cancellationToken).ConfigureAwait(false);
            }
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
        return result with { Stream = new CountingYencStream(result.Stream, bytesTracker, host) };
    }

    private PipelinedArticleResult WrapPipelinedArticle(PipelinedArticleResult result, string host)
    {
        if (bytesTracker == null || result.Stream == null) return result;
        return result with { Stream = new CountingYencStream(result.Stream, bytesTracker, host) };
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
            if (!probe.Success ||
                probe.Found == 0 ||
                !IsPrimaryStatProvider(probe.Provider))
                return;

            lock (_preferred)
            {
                var currentBest = _preferred
                    .Select(provider => _probes.GetValueOrDefault(provider)?.Found ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();
                if (probe.Found < currentBest) return;
                if (probe.Found > currentBest) _preferred.Clear();
                _preferred.Add(probe.Provider);
            }
        }

        public bool IsPreferred(MultiConnectionNntpClient provider)
        {
            lock (_preferred) return _preferred.Contains(provider);
        }

        public int ProbeRank(MultiConnectionNntpClient provider) =>
            _probeRanks.GetValueOrDefault(provider, int.MaxValue);

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
            foreach (var probe in _probes.Values.OrderBy(x => ProbeRank(x.Provider)))
            {
                _attempts.TryGetValue(probe.Provider, out var attempts);
                var snapshot = attempts?.Snapshot() ?? default;
                var rate = snapshot.ElapsedMs > 0
                    ? (int)Math.Round(snapshot.Received * 1000d / snapshot.ElapsedMs)
                    : snapshot.Received;
                Log.Information(
                    "health-stat provider-summary provider={Provider} preferred={Preferred} " +
                    "probeFound={ProbeFound}/{ProbeReceived} batches={Batches} attempted={Attempted} received={Received} " +
                    "found={Found} missing={Missing} failures={Failures} workMs={WorkMs} rate={StatRate}stat/s",
                    probe.Provider.Host, IsPreferred(probe.Provider), probe.Found, probe.Received,
                    snapshot.Batches, snapshot.Attempted, snapshot.Received, snapshot.Found, snapshot.Missing,
                    snapshot.Failures, snapshot.ElapsedMs, rate);
            }
        }
    }

    private sealed class BulkStatAttemptStats
    {
        private long _attempted;
        private long _batches;
        private long _elapsedMs;
        private long _failures;
        private long _found;
        private long _missing;
        private long _received;

        public void Record(int attempted, int received, int found, int missing, long elapsedMs, bool failed)
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

    private readonly record struct BulkStatAttemptSnapshot(
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
