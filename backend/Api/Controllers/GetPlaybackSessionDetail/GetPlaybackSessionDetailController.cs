using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.GetPlaybackSessions;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessionDetail;

/// <summary>
/// One playback session in full, plus the raw article fetches still retained for
/// it. Used by the expanded row on the playback page.
/// </summary>
[ApiController]
[Route("api/get-playback-session-detail")]
public class GetPlaybackSessionDetailController(
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : BaseApiController
{
    // Mirrors MetricsRetentionService.FetchTtl.
    private const int ArticleRetentionHours = 24;
    private const int MaxArticles = 500;

    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var idText = HttpContext.Request.Query["id"].ToString();
        if (!Guid.TryParse(idText, out var sessionId))
            throw new BadHttpRequestException("A valid session id is required.");

        var providersById = configManager.GetUsenetProviderConfig().Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        await using var metrics = new MetricsDbContext();
        var row = await metrics.ReadSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            .ConfigureAwait(false);
        if (row is null) throw new BadHttpRequestException("The playback session does not exist.");

        var fetches = await metrics.SegmentFetches.AsNoTracking()
            .Where(x => x.ReadSessionId == sessionId)
            .OrderByDescending(x => x.At)
            .Take(MaxArticles)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var counts = await metrics.SegmentFetches.AsNoTracking()
            .Where(x => x.ReadSessionId == sessionId)
            .GroupBy(x => new { x.Provider, x.Status })
            .Select(g => new
            {
                g.Key.Provider,
                g.Key.Status,
                Count = g.Count(),
                AvgDurationMs = g.Average(x => (double)x.DurationMs),
                MaxDurationMs = g.Max(x => x.DurationMs),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        string? title = null;
        string? nzbName = null;
        string? category = null;
        if (row.HistoryItemId is { } historyItemId)
        {
            var history = await dbClient.Ctx.HistoryItems.AsNoTracking()
                .Where(x => x.Id == historyItemId)
                .Select(x => new { x.FileName, x.JobName, x.Category })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            title = history?.FileName;
            nzbName = history?.JobName;
            category = history?.Category;
        }
        if (title is null && row.DavItemId is { } davItemId)
        {
            title = await dbClient.Ctx.Items.AsNoTracking()
                .Where(x => x.Id == davItemId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return Ok(new GetPlaybackSessionDetailResponse
        {
            Status = true,
            Session = PlaybackHistory.BuildSession(row, providersById),
            Title = title ?? row.FileName,
            NzbName = nzbName,
            Category = category,
            ArticleDetailAvailable = fetches.Count > 0,
            ArticleDetailExpired = fetches.Count == 0 &&
                                   row.EndedAt < DateTimeOffset.UtcNow
                                       .AddHours(-ArticleRetentionHours)
                                       .ToUnixTimeMilliseconds(),
            ArticleRetentionHours = ArticleRetentionHours,
            Articles = fetches.Select(x =>
            {
                providersById.TryGetValue(x.Provider, out var configured);
                return new GetPlaybackSessionDetailResponse.ArticleFetchDto
                {
                    AtUnix = x.At / 1000,
                    AtMs = x.At,
                    ProviderId = x.Provider,
                    Host = configured?.Host ?? x.Provider,
                    Nickname = configured?.Nickname,
                    Status = x.Status,
                    DurationMs = x.DurationMs,
                    Retries = x.Retries,
                    Bytes = x.Bytes,
                };
            }).ToList(),
            ArticleCounts = counts.Select(x =>
            {
                providersById.TryGetValue(x.Provider, out var configured);
                return new GetPlaybackSessionDetailResponse.ArticleCountDto
                {
                    ProviderId = x.Provider,
                    Host = configured?.Host ?? x.Provider,
                    Nickname = configured?.Nickname,
                    Status = x.Status,
                    Count = x.Count,
                    AvgDurationMs = (int)Math.Round(x.AvgDurationMs),
                    MaxDurationMs = x.MaxDurationMs,
                };
            })
            .OrderByDescending(x => x.Count)
            .ToList(),
        });
    }
}
