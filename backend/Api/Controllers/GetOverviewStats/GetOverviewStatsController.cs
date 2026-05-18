using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.GetOverviewStats;

[ApiController]
[Route("api/get-overview-stats")]
public class GetOverviewStatsController(
    DavDatabaseClient davDb,
    ActiveReadRegistry registry
) : BaseApiController
{
    private const long OneMinute = 60_000;
    private const long OneHour = 60 * OneMinute;

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new GetOverviewStatsRequest(HttpContext);
        var response = await BuildAsync(request).ConfigureAwait(false);
        return Ok(response);
    }

    private async Task<GetOverviewStatsResponse> BuildAsync(GetOverviewStatsRequest request)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var is7d = request.Window == GetOverviewStatsRequest.OverviewWindow.Last7Days;
        var windowMs = is7d ? 7 * 24 * OneHour : 24 * OneHour;
        var windowStart = nowMs - windowMs;
        var bucketSize = is7d ? OneHour : OneMinute;

        await using var metrics = new MetricsDbContext();

        var liveTiles = await BuildLiveTilesAsync(metrics, nowMs).ConfigureAwait(false);
        var throughput = await BuildThroughputAsync(metrics, windowStart, bucketSize).ConfigureAwait(false);
        var providers = await BuildProvidersAsync(metrics, windowStart, is7d).ConfigureAwait(false);
        var catalogue = await BuildCatalogueAsync().ConfigureAwait(false);
        var sessions = await BuildSessionsAsync(metrics, windowStart).ConfigureAwait(false);
        var topReads = await BuildTopReadsAsync(metrics, windowStart).ConfigureAwait(false);

        var totalArticles = throughput.Sum(p => p.Articles);
        var totalErrors = throughput.Sum(p => p.Errors);

        return new GetOverviewStatsResponse
        {
            Window = is7d ? "7d" : "24h",
            Tiles = liveTiles,
            Throughput = throughput,
            TotalArticles = totalArticles,
            TotalErrors = totalErrors,
            Providers = providers,
            Catalogue = catalogue,
            Sessions = sessions,
            TopReads = topReads,
        };
    }

    private async Task<GetOverviewStatsResponse.LiveTiles> BuildLiveTilesAsync(MetricsDbContext metrics, long nowMs)
    {
        var sinceMs = nowMs - OneMinute;
        var articles = await metrics.SegmentFetches.Where(x => x.At >= sinceMs).CountAsync().ConfigureAwait(false);
        var errors = await metrics.SegmentFetches
            .Where(x => x.At >= sinceMs && x.Status != SegmentFetch.FetchStatus.Ok)
            .CountAsync().ConfigureAwait(false);
        var bytesServed = await metrics.ReadSessions
            .Where(x => x.EndedAt >= sinceMs)
            .SumAsync(x => (long?)x.BytesServed).ConfigureAwait(false) ?? 0L;

        return new GetOverviewStatsResponse.LiveTiles
        {
            ActiveReads = registry.Count,
            ArticlesPerMinute = articles,
            ErrorsPerMinute = errors,
            BytesServedPerMinute = bytesServed,
        };
    }

    /// <summary>
    /// Builds the throughput chart directly from raw SegmentFetches so the line
    /// populates the moment any fetch happens (no waiting for the minute
    /// rollup). Bytes-served is overlaid from ReadSessions (lands when a
    /// session prunes).
    /// </summary>
    private static async Task<List<GetOverviewStatsResponse.ThroughputPoint>> BuildThroughputAsync(
        MetricsDbContext metrics, long windowStart, long bucketSize)
    {
        var fetches = await metrics.SegmentFetches
            .Where(x => x.At >= windowStart)
            .Select(x => new { x.At, x.Status })
            .ToListAsync().ConfigureAwait(false);

        var sessions = await metrics.ReadSessions
            .Where(x => x.EndedAt >= windowStart)
            .Select(x => new { x.EndedAt, x.BytesServed })
            .ToListAsync().ConfigureAwait(false);

        // Bucket articles + errors by fetch time.
        var byBucket = new Dictionary<long, (long Articles, long Errors, long BytesServed)>();
        foreach (var f in fetches)
        {
            var b = f.At - (f.At % bucketSize);
            byBucket.TryGetValue(b, out var cur);
            byBucket[b] = (cur.Articles + 1, cur.Errors + (f.Status != SegmentFetch.FetchStatus.Ok ? 1 : 0), cur.BytesServed);
        }
        foreach (var s in sessions)
        {
            var b = s.EndedAt - (s.EndedAt % bucketSize);
            byBucket.TryGetValue(b, out var cur);
            byBucket[b] = (cur.Articles, cur.Errors, cur.BytesServed + s.BytesServed);
        }

        return byBucket
            .OrderBy(kv => kv.Key)
            .Select(kv => new GetOverviewStatsResponse.ThroughputPoint
            {
                Bucket = kv.Key,
                Articles = kv.Value.Articles,
                Errors = kv.Value.Errors,
                BytesServed = kv.Value.BytesServed,
            })
            .ToList();
    }

    private static async Task<List<GetOverviewStatsResponse.ProviderRow>> BuildProvidersAsync(
        MetricsDbContext metrics, long windowStart, bool is7d)
    {
        if (is7d)
        {
            return await metrics.ProviderHourly
                .Where(x => x.Hour >= windowStart)
                .GroupBy(x => x.Provider)
                .Select(g => new GetOverviewStatsResponse.ProviderRow
                {
                    Provider = g.Key,
                    Articles = g.Sum(x => x.Articles),
                    BytesFetched = g.Sum(x => x.BytesFetched),
                    Errors = g.Sum(x => x.Errors),
                    Retries = g.Sum(x => x.Retries),
                    AvgDurationMs = g.Sum(x => x.Articles) > 0
                        ? (double)g.Sum(x => x.SumDurationMs) / g.Sum(x => x.Articles)
                        : 0,
                    ErrorRate = g.Sum(x => x.Articles) > 0
                        ? (double)g.Sum(x => x.Errors) / g.Sum(x => x.Articles)
                        : 0,
                })
                .OrderByDescending(r => r.Articles)
                .ToListAsync().ConfigureAwait(false);
        }

        // For the 24h window, aggregate directly from raw fetches so the table
        // populates immediately (the minute rollup may not have run yet).
        var rows = await metrics.SegmentFetches
            .Where(x => x.At >= windowStart)
            .GroupBy(x => x.Provider)
            .Select(g => new
            {
                Provider = g.Key,
                Articles = (long)g.Count(),
                Errors = (long)g.Count(x => x.Status != SegmentFetch.FetchStatus.Ok),
                Retries = (long)g.Sum(x => x.Retries),
                SumDurationMs = (long)g.Sum(x => x.DurationMs),
            })
            .ToListAsync().ConfigureAwait(false);

        return rows
            .Select(r => new GetOverviewStatsResponse.ProviderRow
            {
                Provider = r.Provider,
                Articles = r.Articles,
                BytesFetched = 0,
                Errors = r.Errors,
                Retries = r.Retries,
                AvgDurationMs = r.Articles > 0 ? (double)r.SumDurationMs / r.Articles : 0,
                ErrorRate = r.Articles > 0 ? (double)r.Errors / r.Articles : 0,
            })
            .OrderByDescending(r => r.Articles)
            .ToList();
    }

    private async Task<GetOverviewStatsResponse.CatalogueBlock> BuildCatalogueAsync()
    {
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var thirtyDaysAgoSec = nowSec - 30L * 24 * 3600;

        var files = davDb.Ctx.Items.Where(i => i.Type == DavItem.ItemType.UsenetFile);
        var fileCount = await files.CountAsync().ConfigureAwait(false);
        var totalBytes = await files.SumAsync(i => (long?)i.FileSize).ConfigureAwait(false) ?? 0L;
        var checkedCount = await davDb.Ctx.Items
            .Where(i => i.Type == DavItem.ItemType.UsenetFile && i.LastHealthCheck.HasValue
                        && i.LastHealthCheck.Value >= DateTimeOffset.FromUnixTimeSeconds(thirtyDaysAgoSec))
            .CountAsync().ConfigureAwait(false);
        var backlog = await davDb.Ctx.HealthCheckResults
            .Where(r => r.RepairStatus == HealthCheckResult.RepairAction.ActionNeeded)
            .CountAsync().ConfigureAwait(false);

        return new GetOverviewStatsResponse.CatalogueBlock
        {
            FileCount = fileCount,
            TotalBytes = totalBytes,
            CheckedPercent = fileCount > 0 ? 100.0 * checkedCount / fileCount : 0,
            RepairBacklog = backlog,
        };
    }

    private static async Task<GetOverviewStatsResponse.SessionsBlock> BuildSessionsAsync(
        MetricsDbContext metrics, long windowStart)
    {
        var summary = await metrics.ReadSessions
            .Where(x => x.EndedAt >= windowStart)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = (long)g.Count(),
                Total = g.Sum(x => x.BytesServed),
                AvgMs = (long)g.Average(x => (double)x.DurationMs),
                LongestMs = (long)g.Max(x => x.DurationMs),
                Biggest = g.Max(x => x.BytesServed),
            })
            .FirstOrDefaultAsync().ConfigureAwait(false);

        if (summary is null) return new GetOverviewStatsResponse.SessionsBlock();

        return new GetOverviewStatsResponse.SessionsBlock
        {
            Count = summary.Count,
            TotalBytesServed = summary.Total,
            AvgDurationMs = summary.AvgMs,
            LongestDurationMs = summary.LongestMs,
            BiggestReadBytes = summary.Biggest,
        };
    }

    private static async Task<List<GetOverviewStatsResponse.TopRead>> BuildTopReadsAsync(
        MetricsDbContext metrics, long windowStart)
    {
        return await metrics.ReadSessions
            .Where(x => x.EndedAt >= windowStart)
            .GroupBy(x => x.Path)
            .Select(g => new GetOverviewStatsResponse.TopRead
            {
                Path = g.Key,
                Reads = g.Count(),
                BytesServed = g.Sum(x => x.BytesServed),
                LastEndedAt = g.Max(x => x.EndedAt),
            })
            .OrderByDescending(t => t.Reads)
            .ThenByDescending(t => t.BytesServed)
            .Take(10)
            .ToListAsync().ConfigureAwait(false);
    }
}
