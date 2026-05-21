using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Variants — keeps multiple size copies of the same content group keyed by
/// (entry.Type, entry.Id). The key is opaque to nzbdav: it never inspects what
/// the strings mean, only whether two clicks resolve to the same identifier.
///
/// Resolution decides, for each play click:
///   1. is there an in-flight download for this content? → wait on it
///   2. is there a completed download in tolerance? → reuse it
///   3. otherwise → let the watchdog fetch a new variant
///
/// Replay selection (≥2 variants) and eviction (capacity bound) live here too.
/// </summary>
public class VariantResolver(ConfigManager configManager)
{
    /// <summary>
    /// Build the content-group key from a cache entry. Opaque to nzbdav — just
    /// joins the two strings the play flow already has. Returns null if either
    /// is empty (defensive; play flow requires both, but legacy/test paths may not).
    /// </summary>
    public static string? BuildContentGroupKey(string? type, string? id)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id)) return null;
        return $"{type}:{id}";
    }

    public static string? BuildContentGroupKey(NzbResolutionCache.Entry entry)
        => BuildContentGroupKey(entry.Type, entry.Id);

    public bool IsEnabled => configManager.GetVariantsMode() != "off";

    /// <summary>
    /// Single per-click decision combining "is there a variant we should reuse?"
    /// and "does the group have any members at all?" The latter tells the caller
    /// whether to skip the legacy filename match (which would otherwise route the
    /// player to the biggest existing video, defeating size-aware collection).
    /// </summary>
    public async Task<VariantDecision> ResolveAsync(
        DavDatabaseContext ctx,
        NzbResolutionCache.Entry entry,
        CancellationToken ct)
    {
        var mode = configManager.GetVariantsMode();
        if (mode == "off") return VariantDecision.NoOp;

        var groupKey = BuildContentGroupKey(entry);
        if (groupKey is null) return VariantDecision.NoOp;

        var variants = await LoadVariantsAsync(ctx, groupKey, ct).ConfigureAwait(false);
        if (variants.Count == 0) return new VariantDecision(null, false, groupKey);

        var clickSize = entry.Primary.Size;
        var match = mode switch
        {
            "smart" => SelectWithinTolerance(variants, clickSize, configManager.GetVariantsTolerancePct()),
            "collect-all" => SelectWithinTolerance(variants, clickSize, exactMatchPct: 5),
            _ => null,
        };
        return new VariantDecision(match, true, groupKey);
    }

    /// <summary>
    /// On watchdog failure with `variants.fallback-on-failure=true`, return the
    /// closest existing variant (no tolerance check). Lets a click that asked for
    /// a size we couldn't fetch still play something rather than 503.
    /// </summary>
    public async Task<VariantMatch?> TryFallbackAfterFailureAsync(
        DavDatabaseContext ctx,
        NzbResolutionCache.Entry entry,
        CancellationToken ct)
    {
        if (!configManager.IsVariantsFallbackOnFailureEnabled()) return null;

        var groupKey = BuildContentGroupKey(entry);
        if (groupKey is null) return null;

        var variants = await LoadVariantsAsync(ctx, groupKey, ct).ConfigureAwait(false);
        if (variants.Count == 0) return null;

        return SelectClosest(variants, entry.Primary.Size);
    }

    /// <summary>
    /// Is there an in-flight QueueItem for this content group? If so, return its id
    /// so the caller can wait on it instead of starting a parallel download.
    /// </summary>
    public async Task<Guid?> FindInFlightAsync(
        DavDatabaseContext ctx,
        NzbResolutionCache.Entry entry,
        CancellationToken ct)
    {
        var groupKey = BuildContentGroupKey(entry);
        if (groupKey is null) return null;

        return await ctx.QueueItems.AsNoTracking()
            .Where(q => q.ContentGroupKey == groupKey)
            .OrderByDescending(q => q.CreatedAt)
            .Select(q => (Guid?)q.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// After a successful download committed by the play flow, enforce the
    /// max-per-group cap by removing surplus variants. Skips any variant played
    /// within the active-grace window (default 60s) — never deletes what the
    /// user is most likely watching right now. Returns the ids of items removed.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> EnforceCapAsync(
        DavDatabaseClient dbClient,
        WebsocketManager? websocketManager,
        string contentGroupKey,
        CancellationToken ct)
    {
        var cap = configManager.GetVariantsMaxPerGroup();
        if (cap <= 0) return Array.Empty<Guid>(); // 0 = unlimited

        var strategy = configManager.GetVariantsEvictionStrategy();
        if (strategy == "never") return Array.Empty<Guid>();

        var variants = await LoadVariantsAsync(dbClient.Ctx, contentGroupKey, ct).ConfigureAwait(false);
        if (variants.Count <= cap) return Array.Empty<Guid>();

        var graceCutoff = DateTimeOffset.UtcNow
            - TimeSpan.FromSeconds(configManager.GetVariantsEvictionActiveGraceSeconds());

        bool IsProtected(VariantRow v) => v.LastPlayedAt is { } t && t >= graceCutoff;

        var ranked = strategy switch
        {
            // Largest-first → drop biggest variants first (keep small copies).
            "largest-first" => variants.OrderByDescending(v => v.LargestFileSize ?? 0).ToList(),
            // Smallest-first → drop smallest first (keep big copies).
            "smallest-first" => variants.OrderBy(v => v.LargestFileSize ?? 0).ToList(),
            // LRU (default) → oldest LastPlayedAt evicted first; never-played sorts oldest.
            _ => variants.OrderBy(v => v.LastPlayedAt ?? DateTimeOffset.MinValue).ToList(),
        };

        var surplus = ranked.Count - cap;
        var toRemove = new List<Guid>(surplus);
        foreach (var v in ranked)
        {
            if (toRemove.Count >= surplus) break;
            if (IsProtected(v)) continue;
            toRemove.Add(v.HistoryItemId);
        }

        if (toRemove.Count == 0) return Array.Empty<Guid>();

        try
        {
            await dbClient.RemoveHistoryItemsAsync(toRemove, deleteFiles: true, ct).ConfigureAwait(false);
            await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
            if (websocketManager is not null)
                _ = websocketManager.SendMessage(
                    WebsocketTopic.HistoryItemRemoved, string.Join(",", toRemove));
            Log.Information(
                "Variants: evicted {Count} surplus variant(s) for group {Group} (strategy={Strategy})",
                toRemove.Count, contentGroupKey, strategy);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Warning(e, "Variants: failed to evict surplus variants for group {Group}", contentGroupKey);
            return Array.Empty<Guid>();
        }

        return toRemove;
    }

    /// <summary>
    /// Bump LastPlayedAt for a HistoryItem we just routed the player to. Fire-and-forget
    /// at the call site — failures here must not block the play redirect.
    /// </summary>
    public async Task MarkPlayedAsync(Guid historyItemId, CancellationToken ct)
    {
        try
        {
            await using var ctx = new DavDatabaseContext();
            var now = DateTimeOffset.UtcNow;
            var item = await ctx.HistoryItems
                .FirstOrDefaultAsync(h => h.Id == historyItemId, ct).ConfigureAwait(false);
            if (item is null) return;
            item.LastPlayedAt = now;
            await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug(e, "Variants: failed to update LastPlayedAt for {Id}", historyItemId);
        }
    }

    public string ReplayStrategy => configManager.GetVariantsReplayStrategy();

    /// <summary>
    /// Replay-time tie-break used when the play handler is choosing between multiple
    /// existing variants (e.g. on watchdog failure fallback). The setting decides
    /// whether intent (closest-to-click) or a fixed preference wins.
    /// </summary>
    public VariantRow? PickReplay(IReadOnlyList<VariantRow> variants, long clickSize)
    {
        if (variants.Count == 0) return null;
        return ReplayStrategy switch
        {
            "largest" => variants.OrderByDescending(v => v.LargestFileSize ?? 0).First(),
            "smallest" => variants.OrderBy(v => v.LargestFileSize ?? long.MaxValue).First(),
            _ => SelectClosest(variants, clickSize)?.Row,
        };
    }

    private static VariantMatch? SelectWithinTolerance(
        IReadOnlyList<VariantRow> variants,
        long clickSize,
        int? tolerancePct = null,
        int? exactMatchPct = null)
    {
        var pct = tolerancePct ?? exactMatchPct ?? 0;
        var closest = SelectClosest(variants, clickSize);
        if (closest is null) return null;

        // Unknown click size (e.g. indexer didn't report it) → trust the closest match
        // we have, regardless of tolerance. Smart mode falls back to reuse rather than
        // pointlessly re-fetching when we can't reason about size differences.
        if (clickSize <= 0) return closest;

        // Closest is exact (or close enough). Reuse.
        var winnerSize = closest.Row.LargestFileSize ?? 0;
        if (winnerSize <= 0) return null;
        var deltaPct = Math.Abs(winnerSize - clickSize) * 100.0 / clickSize;
        return deltaPct <= pct ? closest : null;
    }

    private static VariantMatch? SelectClosest(IReadOnlyList<VariantRow> variants, long clickSize)
    {
        VariantRow? best = null;
        long bestDelta = long.MaxValue;
        DateTimeOffset bestPlayed = DateTimeOffset.MinValue;

        foreach (var v in variants)
        {
            var size = v.LargestFileSize ?? 0;
            // If click size is unknown, treat every variant's delta as 0 — then the
            // tiebreak (LastPlayedAt) and largest/smallest fallback decides.
            var delta = clickSize > 0 ? Math.Abs(size - clickSize) : 0;
            var played = v.LastPlayedAt ?? DateTimeOffset.MinValue;
            if (best is null || delta < bestDelta || (delta == bestDelta && played > bestPlayed))
            {
                best = v;
                bestDelta = delta;
                bestPlayed = played;
            }
        }

        return best is null ? null : new VariantMatch(best, bestDelta);
    }

    private static async Task<List<VariantRow>> LoadVariantsAsync(
        DavDatabaseContext ctx,
        string contentGroupKey,
        CancellationToken ct)
    {
        // Inner join to DavItems to compute the largest file size for each completed
        // HistoryItem in the group. Variants per group are small (≤ max-per-group, plus
        // a few legacy null-key rows are excluded by the WHERE), so this is cheap.
        var rows = await ctx.HistoryItems.AsNoTracking()
            .Where(h => h.ContentGroupKey == contentGroupKey
                        && h.DownloadStatus == HistoryItem.DownloadStatusOption.Completed)
            .Select(h => new
            {
                HistoryItemId = h.Id,
                h.LastPlayedAt,
                h.CreatedAt,
                LargestFileSize = ctx.Items
                    .Where(d => d.HistoryItemId == h.Id && d.Type == DavItem.ItemType.UsenetFile)
                    .Max(d => (long?)d.FileSize),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new VariantRow(r.HistoryItemId, r.LargestFileSize, r.LastPlayedAt, r.CreatedAt))
            .ToList();
    }
}

public sealed record VariantRow(
    Guid HistoryItemId,
    long? LargestFileSize,
    DateTimeOffset? LastPlayedAt,
    DateTime CreatedAt);

public sealed record VariantMatch(VariantRow Row, long SizeDeltaBytes);

/// <summary>
/// Outcome of a per-click variant resolution. ReuseMatch=non-null means redirect
/// to that variant. ReuseMatch=null + GroupHasMembers=true means "fetch a new
/// variant for the same content" (skip the legacy filename match). ReuseMatch=null
/// + GroupHasMembers=false means "no variant context — fall back to legacy/watchdog".
/// </summary>
public sealed record VariantDecision(VariantMatch? ReuseMatch, bool GroupHasMembers, string? GroupKey)
{
    public static readonly VariantDecision NoOp = new(null, false, null);
}
