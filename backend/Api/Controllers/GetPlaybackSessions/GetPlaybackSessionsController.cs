using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessions;

/// <summary>
/// Playback history: what was streamed, from which providers, and what went
/// wrong on the way. Reads the metrics database for the sessions and the
/// operational database for display names — they are separate SQLite files, so
/// the join happens in memory.
/// </summary>
[ApiController]
[Route("api/get-playback-sessions")]
public class GetPlaybackSessionsController(
    DavDatabaseClient dbClient,
    ConfigManager configManager
) : BaseApiController
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 2000;

    /// <summary>
    /// One raw session is not one play: a play is many sessions, and library
    /// scans outnumber real viewing several to one. Grouping runs after the
    /// sample is taken, so the sample has to be deep enough that the plays a
    /// person is looking for survive being outnumbered.
    /// </summary>
    private const int MinSampleForGrouping = 200;

    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var query = HttpContext.Request.Query;
        var limit = int.TryParse(query["limit"].ToString(), out var parsedLimit)
            ? Math.Clamp(parsedLimit, MinSampleForGrouping, MaxLimit)
            : DefaultLimit;
        var sinceMs = long.TryParse(query["sinceUnix"].ToString(), out var sinceUnix)
            ? sinceUnix * 1000
            : (long?)null;
        var filter = query["filter"].ToString();

        var providersById = configManager.GetUsenetProviderConfig().Providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        await using var metrics = new MetricsDbContext();
        var rowQuery = metrics.ReadSessions.AsNoTracking();
        if (sinceMs is { } cutoff) rowQuery = rowQuery.Where(x => x.EndedAt >= cutoff);
        var rows = await rowQuery
            .OrderByDescending(x => x.StartedAt)
            .ThenByDescending(x => x.EndedAt)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sessions = rows
            .Select(row => PlaybackHistory.BuildSession(row, providersById))
            .ToList();
        var content = await ResolveContentAsync(rows, ct).ConfigureAwait(false);

        var plays = PlaybackHistory
            .GroupIntoPlays(sessions, session => LookupContent(session, content))
            .Where(play => PlaybackHistory.MatchesFilter(play, filter))
            .ToList();

        return Ok(new GetPlaybackSessionsResponse
        {
            Status = true,
            Plays = plays,
            SampledSessions = rows.Count,
            Truncated = rows.Count >= limit,
            Limit = limit,
        });
    }

    private static PlaybackContentInfo? LookupContent(
        GetPlaybackSessionsResponse.SessionDto session,
        ContentLookup content)
    {
        if (session.DavItemId is not null &&
            Guid.TryParse(session.DavItemId, out var davItemId) &&
            content.ByDavItem.TryGetValue(davItemId, out var byDavItem))
            return byDavItem;
        if (session.HistoryItemId is not null &&
            Guid.TryParse(session.HistoryItemId, out var historyItemId) &&
            content.ByHistoryItem.TryGetValue(historyItemId, out var byHistoryItem))
            return byHistoryItem;
        return null;
    }

    /// <summary>
    /// Looks up the human-readable names behind the ids stored on each session.
    /// Rows whose content has since been deleted simply keep their stored file
    /// name, so history survives cleanup.
    /// </summary>
    private async Task<ContentLookup> ResolveContentAsync(
        IReadOnlyList<Database.Models.Metrics.ReadSession> rows,
        CancellationToken ct)
    {
        var davItemIds = rows
            .Where(x => x.DavItemId.HasValue)
            .Select(x => x.DavItemId!.Value)
            .Distinct()
            .ToList();
        var historyItemIds = rows
            .Where(x => x.HistoryItemId.HasValue)
            .Select(x => x.HistoryItemId!.Value)
            .Distinct()
            .ToList();

        var byHistoryItem = new Dictionary<Guid, PlaybackContentInfo>();
        if (historyItemIds.Count > 0)
        {
            var historyItems = await dbClient.Ctx.HistoryItems.AsNoTracking()
                .Where(x => historyItemIds.Contains(x.Id))
                .Select(x => new { x.Id, x.FileName, x.JobName, x.Category })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var item in historyItems)
                byHistoryItem[item.Id] = new PlaybackContentInfo(
                    item.FileName, item.JobName, item.Category);
        }

        var byDavItem = new Dictionary<Guid, PlaybackContentInfo>();
        if (davItemIds.Count > 0)
        {
            var davItems = await dbClient.Ctx.Items.AsNoTracking()
                .Where(x => davItemIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Name, x.HistoryItemId })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var item in davItems)
            {
                var history = item.HistoryItemId is { } hid
                    ? byHistoryItem.GetValueOrDefault(hid)
                    : null;
                byDavItem[item.Id] = new PlaybackContentInfo(
                    item.Name, history?.NzbName, history?.Category);
            }
        }

        return new ContentLookup(byDavItem, byHistoryItem);
    }

    private sealed record ContentLookup(
        IReadOnlyDictionary<Guid, PlaybackContentInfo> ByDavItem,
        IReadOnlyDictionary<Guid, PlaybackContentInfo> ByHistoryItem);
}
