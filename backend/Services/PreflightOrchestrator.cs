using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class PreflightOrchestrator(
    ConfigManager configManager,
    PreflightCache preflightCache,
    PlaybackFastVerifier fastVerifier,
    NewznabRateLimiter rateLimiter,
    IndexerHitTracker hitTracker,
    LazyRarResolver lazyRarResolver,
    PreflightSessionRegistry sessionRegistry,
    CandidateNegativeCache negativeCache,
    WardenStore wardenStore,
    NzbFetchCoalescer nzbFetchCoalescer)
{
    public void Start(
        string profileToken,
        string type,
        string id,
        IReadOnlyList<NzbResolutionCache.Candidate> candidates)
    {
        var mode = configManager.GetPreflightMode();
        if (mode == "off" || candidates.Count == 0) return;

        var session = sessionRegistry.BeginSession(profileToken, type, id);

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            var prepared = 0;
            try
            {
                prepared = await PreflightAsync(mode, candidates, session.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Debug(e, "Preflight failed for {Type}/{Id}", type, id);
            }
            finally
            {
                session.Dispose();
                if (prepared > 0)
                    Log.Debug("Preflight readied {Count} candidate(s) for {Type}/{Id} in {Ms}ms (mode={Mode})",
                        prepared, type, id, sw.ElapsedMilliseconds, mode);
            }
        });
    }

    private async Task<int> PreflightAsync(
        string mode,
        IReadOnlyList<NzbResolutionCache.Candidate> candidates,
        CancellationToken ct)
    {
        var maxAttempts = Math.Min(configManager.GetPreflightMaxAttempts(), candidates.Count);
        var maxWait = TimeSpan.FromSeconds(configManager.GetPreflightIndexerMaxWaitSeconds());
        var indexers = configManager.GetIndexerConfig().Indexers
            .Where(x => x.Enabled)
            .SelectMany(x => new[]
            {
                new KeyValuePair<string, IndexerConfig.ConnectionDetails>(x.Id, x),
                new KeyValuePair<string, IndexerConfig.ConnectionDetails>(x.Name, x),
            })
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);
        var hideKnownWardenDead = configManager.IsWardenHideDeadEnabled()
            && candidates.Any(c => !wardenStore.IsDeadAnywhere(
                WardenFingerprint.Compute(c.Size, c.Poster, c.UsenetDate)));

        for (var i = 0; i < maxAttempts; i++)
        {
            if (ct.IsCancellationRequested) return 0;
            var ok = await PreflightCandidateAsync(
                mode, candidates[i], indexers, maxWait, hideKnownWardenDead, ct).ConfigureAwait(false);
            if (ok) return 1;
        }
        return 0;
    }

    private async Task<bool> PreflightCandidateAsync(
        string mode,
        NzbResolutionCache.Candidate candidate,
        IReadOnlyDictionary<string, IndexerConfig.ConnectionDetails> indexers,
        TimeSpan maxWait,
        bool hideKnownWardenDead,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;

        var fp = WardenFingerprint.Compute(candidate.Size, candidate.Poster, candidate.UsenetDate);
        if (hideKnownWardenDead && wardenStore.IsDeadAnywhere(fp)) return false;

        if (!indexers.TryGetValue(candidate.IndexerId ?? candidate.IndexerName, out var indexer)) return false;
        var nzbBytes = await FetchNzbBytesAsync(candidate, indexer, maxWait, ct).ConfigureAwait(false);
        if (nzbBytes is null || ct.IsCancellationRequested) return false;

        PlaybackFastVerifier.VerifyOutcome outcome;
        using (var ms = new MemoryStream(nzbBytes, writable: false))
        {
            outcome = await fastVerifier
                .VerifyAsync(ms, "stat", configManager.GetPreflightVerifySampleCount(), ct)
                .ConfigureAwait(false);
        }

        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Dead)
        {
            negativeCache.MarkFailed(candidate.NzbUrl);
            wardenStore.MarkDead(fp);
            return false;
        }

        var bytesToCache = mode == "light" ? null : nzbBytes;
        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Timeout)
        {
            if (bytesToCache is not null)
                preflightCache.SetVerified(candidate.NzbUrl, bytesToCache, outcome.Verdict, outcome.ResponderHost);
            return false;
        }

        preflightCache.SetVerified(candidate.NzbUrl, bytesToCache, outcome.Verdict, outcome.ResponderHost);

        if (mode == "full" && !ct.IsCancellationRequested)
        {
            await TryPreWarmExistingAsync(candidate, ct).ConfigureAwait(false);
        }

        return true;
    }

    private Task<byte[]?> FetchNzbBytesAsync(
        NzbResolutionCache.Candidate c,
        IndexerConfig.ConnectionDetails indexer,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        return nzbFetchCoalescer.GetOrFetchAsync(
            c.NzbUrl,
            innerCt => rateLimiter.TryWaitAsync(
                indexer.Id, indexer.MaxRequestsPerMinute, maxWait, innerCt),
            async innerCt =>
            {
                try
                {
                    var hitCheck = await hitTracker
                        .ReserveAsync(indexer.Id, IndexerApiHit.HitType.Download,
                            indexer.DownloadLimit, indexer.HitLimitResetTime, innerCt)
                        .ConfigureAwait(false);
                    if (hitCheck is { Allowed: false })
                    {
                        Log.Debug("Preflight skipped for {Indexer}: {Reason}",
                            indexer.Name,
                            IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Download));
                        return null;
                    }

                    using var req = new HttpRequestMessage(HttpMethod.Get, c.NzbUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", c.IndexerUserAgent);
                    var client = ProxyHttpClientPool.GetClient(c.ProxyUrl);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(innerCt);
                    var timeoutSeconds = configManager.GetIndexerConfig().GetEffectiveTimeoutSeconds(indexer);
                    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (!e.IsCancellationException())
                {
                    Log.Debug("Preflight NZB fetch failed for {Url}: {Message}", c.NzbUrl, e.Message);
                    return null;
                }
            }, ct);
    }

    private async Task TryPreWarmExistingAsync(NzbResolutionCache.Candidate candidate, CancellationToken ct)
    {
        try
        {
            var fileName = $"{SanitizeFileName(candidate.Title)}.nzb";
            await using var ctx = new DavDatabaseContext();

            var historyId = await ctx.HistoryItems.AsNoTracking()
                .Where(h => h.FileName == fileName
                            && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => (Guid?)h.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (historyId is null) return;

            var davItem = await ctx.Items.AsNoTracking()
                .Where(x => x.HistoryItemId == historyId.Value
                            && x.Type == DavItem.ItemType.UsenetFile
                            && x.SubType == DavItem.ItemSubType.MultipartFile)
                .OrderByDescending(x => x.FileSize ?? 0)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (davItem is null) return;

            var client = new DavDatabaseClient(ctx);
            var mpf = await client.GetDavMultipartFileAsync(davItem, ct).ConfigureAwait(false);
            if (mpf is null || !mpf.Metadata.IsLazy) return;
            if ((mpf.Metadata.PendingParts?.Length ?? 0) == 0) return;

            await lazyRarResolver.EnsureResolvedThroughAsync(mpf, long.MaxValue, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Preflight lazy pre-warm failed for {Title}", candidate.Title);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "untitled" : clean;
    }
}
