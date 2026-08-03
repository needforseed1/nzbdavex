using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using Serilog;

namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// Builds a pool of real, re-downloadable NNTP message-ids for the speed test to
/// fetch. We reuse articles the user has already downloaded (or queued) rather
/// than inventing synthetic ones, so the test measures the same kind of traffic
/// real downloads produce. Returns an empty list (gracefully) when there's
/// nothing to draw from — the caller falls back to a latency-only result.
/// </summary>
public sealed class BenchmarkCorpusProvider(DavDatabaseClient db)
{
    // Cap how many nzb files / queue entries we crack open. A handful of recent
    // releases already yields thousands of segments — far more than any run needs.
    private const int MaxNzbFilesToScan = 60;
    private const int MaxQueueNzbsToScan = 10;
    private const int MaxHealthHistoryItemsToScan = 10;

    /// <summary>
    /// Builds a coherent health-check corpus from one recently completed NZB.
    /// Bulk health checks operate on one release at a time; mixing unrelated
    /// old files can turn provider-specific retention misses into the dominant
    /// benchmark cost and does not represent the queue path being tuned.
    /// </summary>
    public async Task<List<string>> GetHealthSegmentPoolAsync(
        int maxSegments,
        CancellationToken ct)
    {
        var best = new List<string>();
        try
        {
            var recentBlobIds = await db.Ctx.HistoryItems
                .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Completed &&
                            x.NzbBlobId != null)
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.NzbBlobId!.Value)
                .Take(MaxHealthHistoryItemsToScan * 2)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var blobId in recentBlobIds.Distinct().Take(MaxHealthHistoryItemsToScan))
            {
                ct.ThrowIfCancellationRequested();
                await using var stream = BlobStore.ReadBlob(blobId);
                if (stream is null) continue;

                try
                {
                    var document = await NzbDocument.LoadAsync(stream).ConfigureAwait(false);
                    var candidate = SelectHealthSegments(document, maxSegments);
                    if (candidate.Count > best.Count) best = candidate;
                    if (candidate.Count >= maxSegments) return candidate;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Debug(e, "Skipping unparseable retained NZB during health benchmark corpus build.");
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warning(e, "Coherent health benchmark corpus gathering failed; using fallback corpus.");
        }

        if (best.Count > 0) return best;
        return await GetSegmentPoolAsync(maxSegments, ct).ConfigureAwait(false);
    }

    public async Task<List<string>> GetSegmentPoolAsync(int maxSegments, CancellationToken ct)
    {
        var pool = new List<string>(Math.Min(maxSegments, 4096));
        var seen = new HashSet<string>();

        try
        {
            await CollectFromCompletedFilesAsync(pool, seen, maxSegments, ct).ConfigureAwait(false);

            // Only crack open queued nzbs if completed downloads didn't give us much.
            if (pool.Count < maxSegments / 4)
                await CollectFromQueuedNzbsAsync(pool, seen, maxSegments, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A corpus hiccup shouldn't sink the whole benchmark — degrade to
            // whatever we managed to gather (possibly latency-only).
            Log.Warning(e, "Benchmark corpus gathering failed; using {Count} segments.", pool.Count);
        }

        Shuffle(pool);
        return pool;
    }

    // Primary source: segment ids persisted for previously-downloaded files.
    private async Task CollectFromCompletedFilesAsync(
        List<string> pool, HashSet<string> seen, int maxSegments, CancellationToken ct)
    {
        var recentNzbItems = await db.Ctx.Items
            .Where(x => x.Type == DavItem.ItemType.UsenetFile && x.SubType == DavItem.ItemSubType.NzbFile)
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxNzbFilesToScan)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var item in recentNzbItems)
        {
            if (pool.Count >= maxSegments) return;
            var nzbFile = await db.GetDavNzbFileAsync(item, ct).ConfigureAwait(false);
            if (nzbFile?.SegmentIds is { Length: > 0 } ids)
                AddIds(pool, seen, ids, maxSegments);
        }
    }

    // Fallback source: parse the raw nzb xml of items still sitting in the queue.
    private async Task CollectFromQueuedNzbsAsync(
        List<string> pool, HashSet<string> seen, int maxSegments, CancellationToken ct)
    {
        var queuedNzbs = await db.Ctx.QueueNzbContents
            .Take(MaxQueueNzbsToScan)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var queued in queuedNzbs)
        {
            if (pool.Count >= maxSegments) return;
            if (string.IsNullOrWhiteSpace(queued.NzbContents)) continue;
            try
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(queued.NzbContents));
                var doc = await NzbDocument.LoadAsync(stream).ConfigureAwait(false);
                foreach (var file in doc.Files)
                {
                    AddIds(pool, seen, file.GetSegmentIds(), maxSegments);
                    if (pool.Count >= maxSegments) return;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Debug(e, "Skipping unparseable queued nzb during benchmark corpus build.");
            }
        }
    }

    private static void AddIds(List<string> pool, HashSet<string> seen, IEnumerable<string> ids, int maxSegments)
    {
        foreach (var id in ids)
        {
            if (pool.Count >= maxSegments) return;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (seen.Add(id)) pool.Add(id);
        }
    }

    internal static List<string> SelectHealthSegments(NzbDocument document, int maxSegments)
    {
        var pool = new List<string>(Math.Max(0, maxSegments));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in document.Files.OrderByDescending(x => x.Segments.Count))
        {
            AddIds(pool, seen, file.GetSegmentIds(), maxSegments);
            if (pool.Count >= maxSegments) break;
        }

        return pool;
    }

    // Shuffle so sequential nzb ordering doesn't bias which segments land in the
    // smaller windows, and so retries don't keep hammering the same first article.
    private static void Shuffle(List<string> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
