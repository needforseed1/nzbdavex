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
        var windowMs = request.Window == GetOverviewStatsRequest.OverviewWindow.Last7Days
            ? 7 * 24 * OneHour
            : 24 * OneHour;
        var windowStart = nowMs - windowMs;

        await using var metrics = new MetricsDbContext();

        var liveTiles = await BuildLiveTilesAsync(metrics, nowMs).ConfigureAwait(false);
        var throughput = await BuildThroughputAsync(metrics, windowStart, request.Window).ConfigureAwait(false);
        var providers = await BuildProvidersAsync(metrics, windowStart, request.Window).ConfigureAwait(false);
        var catalogue = await BuildCatalogueAsync().ConfigureAwait(false);
        var repair = await BuildRepairAsync().ConfigureAwait(false);

        var totalFetched = 0L;
        var totalServed = 0L;
        foreach (var p in throughput)
        {
            totalFetched += p.BytesFetched;
            totalServed += p.BytesServed;
        }
        var amplification = totalServed > 0 ? (double)totalFetched / totalServed : 0;

        return new GetOverviewStatsResponse
        {
            Window = request.Window == GetOverviewStatsRequest.OverviewWindow.Last7Days ? "7d" : "24h",
            Tiles = liveTiles,
            Throughput = throughput,
            ReadAmplification = amplification,
            Providers = providers,
            Catalogue = catalogue,
            Repair = repair,
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

    private static async Task<List<GetOverviewStatsResponse.ThroughputPoint>> BuildThroughputAsync(
        MetricsDbContext metrics, long windowStart, GetOverviewStatsRequest.OverviewWindow window)
    {
        if (window == GetOverviewStatsRequest.OverviewWindow.Last24Hours)
        {
            return await metrics.ThroughputMinutes
                .Where(x => x.Minute >= windowStart)
                .OrderBy(x => x.Minute)
                .Select(x => new GetOverviewStatsResponse.ThroughputPoint
                {
                    Bucket = x.Minute,
                    BytesServed = x.BytesServed,
                    BytesFetched = x.BytesFetched,
                    Articles = x.Articles,
                    Errors = x.Errors,
                })
                .ToListAsync().ConfigureAwait(false);
        }

        // 7d view: bucket the minute rollup into hours so the chart stays under ~168 points.
        var minutes = await metrics.ThroughputMinutes
            .Where(x => x.Minute >= windowStart)
            .ToListAsync().ConfigureAwait(false);

        return minutes
            .GroupBy(x => x.Minute - (x.Minute % OneHour))
            .OrderBy(g => g.Key)
            .Select(g => new GetOverviewStatsResponse.ThroughputPoint
            {
                Bucket = g.Key,
                BytesServed = g.Sum(x => x.BytesServed),
                BytesFetched = g.Sum(x => x.BytesFetched),
                Articles = g.Sum(x => x.Articles),
                Errors = g.Sum(x => x.Errors),
            })
            .ToList();
    }

    private static async Task<List<GetOverviewStatsResponse.ProviderRow>> BuildProvidersAsync(
        MetricsDbContext metrics, long windowStart, GetOverviewStatsRequest.OverviewWindow window)
    {
        if (window == GetOverviewStatsRequest.OverviewWindow.Last7Days)
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

        return await metrics.ProviderMinutes
            .Where(x => x.Minute >= windowStart)
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

    private async Task<GetOverviewStatsResponse.RepairBlock> BuildRepairAsync()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);

        var stats = await davDb.Ctx.HealthCheckStats
            .Where(s => s.DateStartInclusive >= cutoff)
            .ToListAsync().ConfigureAwait(false);

        long healthy = 0, repaired = 0, deleted = 0, actionNeeded = 0;
        foreach (var s in stats)
        {
            switch (s.RepairStatus)
            {
                case HealthCheckResult.RepairAction.None when s.Result == HealthCheckResult.HealthResult.Healthy:
                    healthy += s.Count; break;
                case HealthCheckResult.RepairAction.Repaired:
                    repaired += s.Count; break;
                case HealthCheckResult.RepairAction.Deleted:
                    deleted += s.Count; break;
                case HealthCheckResult.RepairAction.ActionNeeded:
                    actionNeeded += s.Count; break;
            }
        }

        return new GetOverviewStatsResponse.RepairBlock
        {
            Healthy = healthy,
            Repaired = repaired,
            Deleted = deleted,
            ActionNeeded = actionNeeded,
        };
    }
}
