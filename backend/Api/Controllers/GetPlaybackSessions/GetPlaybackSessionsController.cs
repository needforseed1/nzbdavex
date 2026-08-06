using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Api.Controllers.GetPlaybackSessions;

/// <summary>
/// Playback history: what NzbDAVex served, from which providers, and what went
/// wrong on the way. Optional Plex metadata only annotates these existing read
/// rows; Plex sessions are not a second history source.
/// </summary>
[ApiController]
[Route("api/get-playback-sessions")]
public class GetPlaybackSessionsController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    PlexReadAttributionMonitor plexMonitor
) : BaseApiController
{
    private const int DefaultLimit = 500;
    private const int MaxLimit = 2000;
    private const int DefaultDeepPlaybackLimit = 200;
    private const int MaxDeepPlaybackLimit = 500;

    /// <summary>
    /// One raw session is not one play: a play is many sessions, and tiny probes
    /// outnumber real viewing several to one. Grouping runs after the sample is
    /// taken, so the sample must be deep enough for useful reads to survive.
    /// </summary>
    private const int MinSampleForGrouping = 200;

    protected override async Task<IActionResult> HandleRequest()
    {
        var ct = HttpContext.RequestAborted;
        var query = HttpContext.Request.Query;
        var filter = query["filter"].ToString();
        var deepPlayback = IsDeepPlaybackHistory(filter, query["deep"].ToString());
        var requestedLimit = int.TryParse(query["limit"].ToString(), out var parsedLimit)
            ? parsedLimit
            : deepPlayback ? DefaultDeepPlaybackLimit : DefaultLimit;
        var limit = deepPlayback
            ? Math.Clamp(requestedLimit, 1, MaxDeepPlaybackLimit)
            : Math.Clamp(requestedLimit, MinSampleForGrouping, MaxLimit);
        var sinceMs = long.TryParse(query["sinceUnix"].ToString(), out var sinceUnix)
            ? sinceUnix * 1000
            : (long?)null;

        var providersById = configManager.GetUsenetProviderConfig().Providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Id))
            .GroupBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        await using var metrics = new MetricsDbContext();
        var rowQuery = metrics.ReadSessions.AsNoTracking();
        if (sinceMs is { } cutoff)
            rowQuery = rowQuery.Where(row => row.EndedAt >= cutoff);
        var orderedRows = rowQuery
            .OrderByDescending(row => row.StartedAt)
            .ThenByDescending(row => row.EndedAt);
        var rows = await ApplyRawSessionLimit(orderedRows, deepPlayback, limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sessions = rows
            .Select(row => PlaybackHistory.BuildSession(row, providersById))
            .ToList();
        var content = await ResolveContentAsync(rows, ct).ConfigureAwait(false);
        var matchingPlays = PlaybackHistory
            .GroupIntoPlays(sessions, session => LookupContent(session, content))
            .Where(play => PlaybackHistory.MatchesFilter(play, filter))
            .ToList();
        var plays = deepPlayback
            ? matchingPlays.Take(limit).ToList()
            : matchingPlays;
        var plexStatus = plexMonitor.GetStatus();

        return Ok(new GetPlaybackSessionsResponse
        {
            Status = true,
            Plays = plays,
            PlexStatus = new GetPlaybackSessionsResponse.PlexStatusDto
            {
                Enabled = plexStatus.Enabled,
                Connected = plexStatus.Connected,
                LastSuccessfulPollAtUnix = plexStatus.LastSuccessfulPollAt is { } lastPoll
                    ? lastPoll / 1000
                    : null,
                LastError = plexStatus.LastError,
                ServerName = plexStatus.ServerName,
                ServerVersion = plexStatus.ServerVersion,
                ActivitiesConnected = plexStatus.ActivitiesConnected,
                ActivitiesError = plexStatus.ActivitiesError,
            },
            SampledSessions = rows.Count,
            Truncated = deepPlayback
                ? matchingPlays.Count > limit
                : rows.Count >= limit,
            Limit = limit,
        });
    }

    internal static bool IsDeepPlaybackHistory(string? filter, string? deep) =>
        string.Equals(deep, "true", StringComparison.OrdinalIgnoreCase)
        && filter is not null
        && (filter.Equals("playback", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("plays", StringComparison.OrdinalIgnoreCase));

    internal static IQueryable<T> ApplyRawSessionLimit<T>(
        IQueryable<T> orderedRows,
        bool deepPlayback,
        int limit) =>
        deepPlayback ? orderedRows : orderedRows.Take(limit);

    private static PlaybackContentInfo? LookupContent(
        GetPlaybackSessionsResponse.SessionDto session,
        ContentLookup content)
    {
        if (session.DavItemId is not null
            && Guid.TryParse(session.DavItemId, out var davItemId)
            && content.ByDavItem.TryGetValue(davItemId, out var byDavItem))
            return byDavItem;
        if (session.HistoryItemId is not null
            && Guid.TryParse(session.HistoryItemId, out var historyItemId)
            && content.ByHistoryItem.TryGetValue(historyItemId, out var byHistoryItem))
            return byHistoryItem;
        return null;
    }

    /// <summary>
    /// Looks up the human-readable names behind the ids stored on each session.
    /// Rows whose content has since been deleted keep their stored file name.
    /// </summary>
    private async Task<ContentLookup> ResolveContentAsync(
        IReadOnlyList<Database.Models.Metrics.ReadSession> rows,
        CancellationToken ct)
    {
        var davItemIds = rows
            .Where(row => row.DavItemId.HasValue)
            .Select(row => row.DavItemId!.Value)
            .Distinct()
            .ToList();
        var historyItemIds = rows
            .Where(row => row.HistoryItemId.HasValue)
            .Select(row => row.HistoryItemId!.Value)
            .Distinct()
            .ToList();

        var byHistoryItem = new Dictionary<Guid, PlaybackContentInfo>();
        if (historyItemIds.Count > 0)
        {
            var historyItems = await dbClient.Ctx.HistoryItems.AsNoTracking()
                .Where(item => historyItemIds.Contains(item.Id))
                .Select(item => new
                {
                    item.Id,
                    item.FileName,
                    item.JobName,
                    item.Category,
                    item.CreatedAt,
                    item.SubmissionSource,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var item in historyItems)
                byHistoryItem[item.Id] = new PlaybackContentInfo(
                    item.FileName,
                    item.JobName,
                    item.Category,
                    ToUnixSeconds(item.CreatedAt),
                    item.SubmissionSource);
        }

        var byDavItem = new Dictionary<Guid, PlaybackContentInfo>();
        if (davItemIds.Count > 0)
        {
            var davItems = await dbClient.Ctx.Items.AsNoTracking()
                .Where(item => davItemIds.Contains(item.Id))
                .Select(item => new
                {
                    item.Id,
                    item.Name,
                    item.HistoryItemId,
                    item.CreatedAt,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var item in davItems)
            {
                var history = item.HistoryItemId is { } historyId
                    ? byHistoryItem.GetValueOrDefault(historyId)
                    : null;
                byDavItem[item.Id] = new PlaybackContentInfo(
                    item.Name,
                    history?.NzbName,
                    history?.Category,
                    history?.CompletedAtUnix ?? ToUnixSeconds(item.CreatedAt),
                    history?.SubmissionSource);
            }
        }

        return new ContentLookup(byDavItem, byHistoryItem);
    }

    private static long ToUnixSeconds(DateTime value)
    {
        // History/DAV rows are intentionally written with DateTime.Now. SQLite
        // returns them as Unspecified, so restore local-time semantics before
        // comparing them with the UTC Unix timestamps in the metrics database.
        if (value.Kind == DateTimeKind.Unspecified)
            value = DateTime.SpecifyKind(value, DateTimeKind.Local);
        return new DateTimeOffset(value).ToUnixTimeSeconds();
    }

    private sealed record ContentLookup(
        IReadOnlyDictionary<Guid, PlaybackContentInfo> ByDavItem,
        IReadOnlyDictionary<Guid, PlaybackContentInfo> ByHistoryItem);
}
