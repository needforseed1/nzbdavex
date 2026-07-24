using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.SabControllers.GetHistory;

public class GetHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    ProviderUsageTracker providerUsageTracker
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetHistoryResponse> GetHistoryAsync(GetHistoryRequest request)
    {
        // get query
        IQueryable<HistoryItem> query = dbClient.Ctx.HistoryItems;
        if (request.NzoIds.Count > 0)
            query = query.Where(q => request.NzoIds.Contains(q.Id));
        var isStreamingClient = SabRequestSource.IsStreamingApiKey(
            httpContext.GetRequestApiKey(), configManager.GetApiKey());
        query = HistoryCategoryClassifier.ApplyFilter(
            query, request.Category, usePhysicalCategories: isStreamingClient);

        // get total count
        var totalCountPromise = query
            .CountAsync(request.CancellationToken);
        var categoriesQuery = dbClient.Ctx.HistoryItems
            .Select(q => new { q.Category, q.SubmissionSource })
            .Distinct()
            .OrderBy(item => item.Category);

        // get history items
        var historyItemsPromise = query
            .OrderByDescending(q => q.CreatedAt)
            .Skip(request.Start)
            .Take(request.Limit)
            .ToArrayAsync(request.CancellationToken);

        // await results
        var totalCount = await totalCountPromise.ConfigureAwait(false);
        var historyItems = await historyItemsPromise.ConfigureAwait(false);
        var categoryPairs = await categoriesQuery
            .ToArrayAsync(request.CancellationToken).ConfigureAwait(false);
        var categories = categoryPairs
            .Select(item => HistoryCategoryClassifier.GetDisplayCategory(
                item.Category, item.SubmissionSource))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToList();

        // get download folders
        var downloadFolderIds = historyItems.Select(x => x.DownloadDirId).ToHashSet();
        var davItems = await dbClient.Ctx.Items
            .Where(x => downloadFolderIds.Contains(x.Id))
            .ToArrayAsync(request.CancellationToken).ConfigureAwait(false);
        var davItemsDict = davItems
            .ToDictionary(x => x.Id, x => x);

        // get slots (in-memory provider counts only survive until app restart)
        var providerUsages = providerUsageTracker.SnapshotMany(historyItems.Select(x => x.Id));
        var nicknamesByHost = configManager.GetUsenetProviderConfig().Providers
            .SelectMany(p => new[]
            {
                new KeyValuePair<string, string?>(p.Id, p.Nickname ?? p.Host),
                new KeyValuePair<string, string?>(p.Host, p.Nickname),
            })
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        var slots = historyItems
            .Select(x =>
            {
                var displayUsage = ProviderUsageTracker.ToDisplayHosts(
                    providerUsages.GetValueOrDefault(x.Id) ?? new Dictionary<string, long>(),
                    configManager.GetUsenetProviderConfig().Providers);
                return GetHistoryResponse.HistorySlot.FromHistoryItem(
                    x,
                    x.DownloadDirId != null ? davItemsDict.GetValueOrDefault(x.DownloadDirId.Value) : null,
                    configManager,
                    displayUsage,
                    nicknamesByHost
                );
            })
            .ToList();

        // return response
        return new GetHistoryResponse()
        {
            History = new GetHistoryResponse.HistoryObject()
            {
                Slots = slots,
                TotalCount = totalCount,
                Categories = categories,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetHistoryRequest(httpContext, configManager);
        return Ok(await GetHistoryAsync(request).ConfigureAwait(false));
    }
}
