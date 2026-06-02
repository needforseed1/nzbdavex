using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public class WatchtowerService(
    ConfigManager configManager,
    SearchProfileService searchProfileService,
    PlaybackFastVerifier fastVerifier,
    IndexerHitTracker hitTracker,
    NewznabRateLimiter rateLimiter,
    CandidateNegativeCache negativeCache,
    PreflightCache preflightCache,
    ListSourceEnumerator enumerator
) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NzbFetchTimeout = TimeSpan.FromSeconds(15);
    private const int ResolvesPerTick = 3;
    private const int KeepFreshPerTick = 5;

    private int _resolveDayKey = -1;
    private int _resolvesToday;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!configManager.IsWatchtowerEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await SyncDueSourcesAsync(stoppingToken).ConfigureAwait(false);
                await ResolveDueItemsAsync(stoppingToken).ConfigureAwait(false);
                await KeepFreshDueItemsAsync(stoppingToken).ConfigureAwait(false);

                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered() || stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Error(e, "Watchtower loop error: {Message}", e.Message);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SyncDueSourcesAsync(CancellationToken ct)
    {
        var now = Now();
        var interval = configManager.GetWatchtowerSyncIntervalSeconds();

        await using var ctx = new DavDatabaseContext();
        var sources = await ctx.ListSources
            .Where(s => s.Enabled && s.Kind != ListSource.KindManual)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var source in sources)
        {
            if (source.LastSyncedAtUnix is { } last && now - last < interval) continue;
            try
            {
                var refs = await enumerator.EnumerateAsync(source, ct).ConfigureAwait(false);
                await ReconcileSourceAsync(ctx, source, refs, now, ct).ConfigureAwait(false);
                source.LastSyncError = null;
            }
            catch (Exception e)
            {
                source.LastSyncError = e.Message;
                Log.Warning(e, "Watchtower: sync failed for source {Name}", source.Name);
            }
            source.LastSyncedAtUnix = now;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ReconcileSourceAsync(
        DavDatabaseContext ctx, ListSource source, IReadOnlyList<WtContentRef> refs, long now, CancellationToken ct)
    {
        var srcId = source.Id.ToString();

        var yielded = new Dictionary<string, WtContentRef>();
        foreach (var r in refs)
        {
            if (string.IsNullOrWhiteSpace(r.Type) || string.IsNullOrWhiteSpace(r.ContentId)) continue;
            yielded[$"{r.Type}:{r.ContentId}"] = r;
        }

        foreach (var (key, r) in yielded)
        {
            var item = await ctx.WantedItems.FirstOrDefaultAsync(w => w.Key == key, ct).ConfigureAwait(false);
            if (item is null)
            {
                ctx.WantedItems.Add(new WantedItem
                {
                    Id = Guid.NewGuid(),
                    Key = key,
                    Type = r.Type,
                    ContentId = r.ContentId,
                    Title = string.IsNullOrWhiteSpace(r.Title) ? r.ContentId : r.Title!,
                    State = WantedItem.StateScouting,
                    Provenance = WtJson.WriteStrings(new[] { srcId }),
                    Shortlist = "[]",
                    CreatedAtUnix = now,
                    UpdatedAtUnix = now,
                    NextCheckAtUnix = now,
                });
            }
            else
            {
                var prov = WtJson.ReadStrings(item.Provenance);
                if (!prov.Contains(srcId))
                {
                    prov.Add(srcId);
                    item.Provenance = WtJson.WriteStrings(prov);
                    item.UpdatedAtUnix = now;
                }
                if (string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(r.Title))
                    item.Title = r.Title!;
            }
        }

        var previouslyClaimed = await ctx.WantedItems
            .Where(w => w.Provenance.Contains(srcId))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in previouslyClaimed)
        {
            if (yielded.ContainsKey(item.Key)) continue;
            var prov = WtJson.ReadStrings(item.Provenance);
            prov.Remove(srcId);
            if (prov.Count == 0)
            {
                ctx.WantedItems.Remove(item);
            }
            else
            {
                item.Provenance = WtJson.WriteStrings(prov);
                item.UpdatedAtUnix = now;
            }
        }
    }

    private async Task ResolveDueItemsAsync(CancellationToken ct)
    {
        var dailyBudget = configManager.GetWatchtowerDailyResolveBudget();
        RollDailyBudget();
        var budgetRoom = dailyBudget == 0 ? int.MaxValue : dailyBudget - _resolvesToday;
        if (budgetRoom <= 0) return;

        var profileToken = ResolveProfileToken();
        if (profileToken is null)
        {
            Log.Debug("Watchtower: no search profile configured; skipping resolve");
            return;
        }

        var now = Now();
        await using var ctx = new DavDatabaseContext();

        var cap = configManager.GetWatchtowerActiveSetCap();
        var activeReady = await ctx.WantedItems.CountAsync(w => w.State == WantedItem.StateReady, ct).ConfigureAwait(false);
        var capRoom = cap - activeReady;
        if (capRoom <= 0) return;

        var take = Math.Min(ResolvesPerTick, Math.Min(capRoom, budgetRoom));
        if (take <= 0) return;

        var due = await ctx.WantedItems
            .Where(w => w.State == WantedItem.StateScouting
                        || (w.State == WantedItem.StateUnavailable
                            && w.NextCheckAtUnix != null && w.NextCheckAtUnix <= now))
            .OrderByDescending(w => w.CreatedAtUnix)
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var item in due)
        {
            if (ct.IsCancellationRequested) break;
            await ResolveOneAsync(profileToken, item, ct).ConfigureAwait(false);
            _resolvesToday++;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task ResolveOneAsync(string profileToken, WantedItem item, CancellationToken ct)
    {
        var now = Now();
        var search = await searchProfileService
            .SearchByImdbAsync(profileToken, item.Type, item.ContentId, ct)
            .ConfigureAwait(false);
        var candidates = search?.Candidates ?? (IReadOnlyList<NzbResolutionCache.Candidate>)Array.Empty<NzbResolutionCache.Candidate>();

        var floor = configManager.GetWatchtowerSizeFloorBytes();
        var ceiling = configManager.GetWatchtowerSizeCeilingBytes();
        var minGrabs = configManager.GetWatchtowerMinGrabs();

        var filtered = candidates
            .Where(c => (c.Password ?? 0) == 0)
            .Where(c => floor <= 0 || c.Size <= 0 || c.Size >= floor)
            .Where(c => ceiling <= 0 || c.Size <= 0 || c.Size <= ceiling)
            .Where(c => minGrabs <= 0 || (c.Grabs ?? 0) >= minGrabs);
        IEnumerable<NzbResolutionCache.Candidate> ordered =
            configManager.GetWatchtowerRanking() == "largest"
                ? filtered.OrderByDescending(c => c.Size)
                : filtered;
        var ranked = ordered.ToList();

        var depth = configManager.GetWatchtowerShortlistDepth();
        var grabCap = configManager.GetWatchtowerGrabCapPerResolve();
        var sample = configManager.GetWatchtowerVerifySampleCount();

        var shortlist = new List<WtPointer>();
        byte[]? winnerBytes = null;
        string? responderHost = null;
        var grabs = 0;

        foreach (var c in ranked)
        {
            if (shortlist.Count >= depth || grabs >= grabCap || ct.IsCancellationRequested) break;
            if (negativeCache.IsFailed(c.NzbUrl)) continue;

            var bytes = await FetchNzbBytesAsync(c, ct).ConfigureAwait(false);
            grabs++;
            if (bytes is null) continue;

            PlaybackFastVerifier.VerifyOutcome outcome;
            using (var ms = new MemoryStream(bytes, writable: false))
                outcome = await fastVerifier.VerifyAsync(ms, "stat", sample, ct).ConfigureAwait(false);

            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Dead)
            {
                negativeCache.MarkFailed(c.NzbUrl);
                continue;
            }
            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Timeout) continue;

            var ptr = new WtPointer
            {
                NzbUrl = c.NzbUrl,
                IndexerName = c.IndexerName,
                IndexerUserAgent = c.IndexerUserAgent,
                ProxyUrl = c.ProxyUrl,
                Title = c.Title,
                Size = c.Size,
                Grabs = c.Grabs,
                Verdict = "available",
                LastVerifiedAtUnix = now,
            };
            if (shortlist.Count == 0)
            {
                winnerBytes = bytes;
                responderHost = outcome.ResponderHost;
            }
            shortlist.Add(ptr);
        }

        if (shortlist.Count > 0)
        {
            item.State = WantedItem.StateReady;
            item.Shortlist = WtJson.WritePointers(shortlist);
            item.WinnerNzb = winnerBytes;
            item.ResponderHost = responderHost;
            item.LastResolvedAtUnix = now;
            item.LastVerifiedAtUnix = now;
            item.NextCheckAtUnix = now + configManager.GetWatchtowerKeepFreshBaseSeconds();
            item.FailReason = null;
            item.UpdatedAtUnix = now;
            preflightCache.SetVerified(shortlist[0].NzbUrl, winnerBytes,
                PlaybackFastVerifier.Verdict.Available, responderHost);
            Log.Information("Watchtower: ready {Key} -> {Title} ({Size} bytes, {Count} pointer(s))",
                item.Key, shortlist[0].Title, shortlist[0].Size, shortlist.Count);
        }
        else
        {
            item.State = WantedItem.StateUnavailable;
            item.Shortlist = "[]";
            item.WinnerNzb = null;
            item.FailReason = candidates.Count == 0 ? "No releases found" : "No healthy release found";
            item.NextCheckAtUnix = now + configManager.GetWatchtowerUnavailableRetrySeconds();
            item.UpdatedAtUnix = now;
            Log.Debug("Watchtower: unavailable {Key} ({Reason})", item.Key, item.FailReason);
        }
    }

    private async Task KeepFreshDueItemsAsync(CancellationToken ct)
    {
        var now = Now();
        await using var ctx = new DavDatabaseContext();
        var due = await ctx.WantedItems
            .Where(w => w.State == WantedItem.StateReady
                        && w.NextCheckAtUnix != null && w.NextCheckAtUnix <= now)
            .OrderBy(w => w.NextCheckAtUnix)
            .Take(KeepFreshPerTick)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var item in due)
        {
            if (ct.IsCancellationRequested) break;
            await KeepFreshOneAsync(item, ct).ConfigureAwait(false);
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task KeepFreshOneAsync(WantedItem item, CancellationToken ct)
    {
        var now = Now();
        var shortlist = WtJson.ReadPointers(item.Shortlist);
        if (shortlist.Count == 0 || item.WinnerNzb is null)
        {
            item.State = WantedItem.StateScouting;
            item.NextCheckAtUnix = now;
            item.UpdatedAtUnix = now;
            return;
        }

        var sample = configManager.GetWatchtowerVerifySampleCount();

        PlaybackFastVerifier.VerifyOutcome outcome;
        using (var ms = new MemoryStream(item.WinnerNzb, writable: false))
            outcome = await fastVerifier.VerifyAsync(ms, "stat", sample, ct).ConfigureAwait(false);

        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Available)
        {
            item.LastVerifiedAtUnix = now;
            item.ResponderHost = outcome.ResponderHost;
            shortlist[0].Verdict = "available";
            shortlist[0].LastVerifiedAtUnix = now;
            item.Shortlist = WtJson.WritePointers(shortlist);
            item.NextCheckAtUnix = now + NextBackoff(item, now);
            item.UpdatedAtUnix = now;
            preflightCache.SetVerified(shortlist[0].NzbUrl, item.WinnerNzb,
                PlaybackFastVerifier.Verdict.Available, outcome.ResponderHost);
            return;
        }

        if (outcome.Verdict == PlaybackFastVerifier.Verdict.Timeout)
        {
            item.NextCheckAtUnix = now + Math.Min(configManager.GetWatchtowerKeepFreshBaseSeconds(), 1800);
            item.UpdatedAtUnix = now;
            return;
        }

        negativeCache.MarkFailed(shortlist[0].NzbUrl);
        shortlist.RemoveAt(0);
        await PromoteBackupAsync(item, shortlist, now, ct).ConfigureAwait(false);
    }

    private async Task PromoteBackupAsync(WantedItem item, List<WtPointer> shortlist, long now, CancellationToken ct)
    {
        var sample = configManager.GetWatchtowerVerifySampleCount();

        while (shortlist.Count > 0 && !ct.IsCancellationRequested)
        {
            var ptr = shortlist[0];
            var bytes = await FetchNzbBytesAsync(MakeCandidate(ptr), ct).ConfigureAwait(false);
            if (bytes is null) { shortlist.RemoveAt(0); continue; }

            PlaybackFastVerifier.VerifyOutcome outcome;
            using (var ms = new MemoryStream(bytes, writable: false))
                outcome = await fastVerifier.VerifyAsync(ms, "stat", sample, ct).ConfigureAwait(false);

            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Available)
            {
                ptr.Verdict = "available";
                ptr.LastVerifiedAtUnix = now;
                item.WinnerNzb = bytes;
                item.ResponderHost = outcome.ResponderHost;
                item.Shortlist = WtJson.WritePointers(shortlist);
                item.LastVerifiedAtUnix = now;
                item.NextCheckAtUnix = now + configManager.GetWatchtowerKeepFreshBaseSeconds();
                item.UpdatedAtUnix = now;
                preflightCache.SetVerified(ptr.NzbUrl, bytes,
                    PlaybackFastVerifier.Verdict.Available, outcome.ResponderHost);
                Log.Information("Watchtower: promoted backup for {Key} -> {Title}", item.Key, ptr.Title);
                return;
            }

            if (outcome.Verdict == PlaybackFastVerifier.Verdict.Dead) negativeCache.MarkFailed(ptr.NzbUrl);
            shortlist.RemoveAt(0);
        }

        item.State = WantedItem.StateScouting;
        item.Shortlist = "[]";
        item.WinnerNzb = null;
        item.NextCheckAtUnix = now;
        item.UpdatedAtUnix = now;
        Log.Debug("Watchtower: shortlist exhausted for {Key}; re-resolving", item.Key);
    }

    private async Task<byte[]?> FetchNzbBytesAsync(NzbResolutionCache.Candidate c, CancellationToken ct)
    {
        try
        {
            var indexer = configManager.GetIndexerConfig().Indexers
                .FirstOrDefault(x => x.Name == c.IndexerName);
            if (indexer is not null)
            {
                var hitCheck = await hitTracker
                    .CheckAsync(c.IndexerName, IndexerApiHit.HitType.Download, indexer.DownloadLimit, indexer.HitLimitResetTime, ct)
                    .ConfigureAwait(false);
                if (hitCheck is { Allowed: false })
                {
                    Log.Information("Watchtower: NZB download skipped for {Indexer}: {Reason}",
                        c.IndexerName, IndexerHitTracker.FormatSkipReason(hitCheck, IndexerApiHit.HitType.Download));
                    return null;
                }
                await rateLimiter.WaitAsync(c.IndexerName, indexer.MaxRequestsPerMinute, ct).ConfigureAwait(false);
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, c.NzbUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", c.IndexerUserAgent);
            var client = ProxyHttpClientPool.GetClient(c.ProxyUrl);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(NzbFetchTimeout);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            _ = hitTracker.RecordAsync(c.IndexerName, IndexerApiHit.HitType.Download, CancellationToken.None);
            return bytes;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Watchtower: NZB fetch failed for {Url}", c.NzbUrl);
            return null;
        }
    }

    private static NzbResolutionCache.Candidate MakeCandidate(WtPointer p) => new()
    {
        IndexerName = p.IndexerName,
        IndexerUserAgent = p.IndexerUserAgent,
        NzbUrl = p.NzbUrl,
        Title = p.Title,
        Size = p.Size,
        Grabs = p.Grabs,
        ProxyUrl = p.ProxyUrl,
    };

    private long NextBackoff(WantedItem item, long now)
    {
        var baseSec = configManager.GetWatchtowerKeepFreshBaseSeconds();
        var maxSec = configManager.GetWatchtowerKeepFreshMaxSeconds();
        var sinceResolved = item.LastResolvedAtUnix is { } r ? Math.Max(0, now - r) : baseSec;
        return Math.Clamp(sinceResolved, baseSec, maxSec);
    }

    private string? ResolveProfileToken()
    {
        var profiles = configManager.GetProfileConfig().Profiles;
        var configured = configManager.GetWatchtowerProfileToken();
        if (!string.IsNullOrEmpty(configured) && profiles.Any(p => p.Token == configured)) return configured;
        return profiles.FirstOrDefault()?.Token;
    }

    private void RollDailyBudget()
    {
        var dayKey = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400);
        if (dayKey != _resolveDayKey)
        {
            _resolveDayKey = dayKey;
            _resolvesToday = 0;
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
