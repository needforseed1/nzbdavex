using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    ProviderUsageTracker providerUsageTracker
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetQueueResponse> GetQueueAsync(GetQueueRequest request)
    {
        // get in progress item
        var (inProgressQueueItem, progressPercentage) = queueManager.GetInProgressQueueItem();

        // get total count
        var ct = request.CancellationToken;
        var totalCount = await dbClient.GetQueueItemsCount(request.Category, ct).ConfigureAwait(false);

        // get queued items
        var getQueueItemsTask = dbClient.GetQueueItems(request.Category, request.Start, request.Limit, ct);
        var queueItems = (await getQueueItemsTask.ConfigureAwait(false))
            .Where(x => x.Id != inProgressQueueItem?.Id)
            .ToArray();

        var configuredProviders = configManager.GetUsenetProviderConfig().Providers;
        var nicknamesByHost = configuredProviders
            .SelectMany(p => new[]
            {
                new KeyValuePair<string, string?>(p.Id, p.Nickname ?? p.Host),
                new KeyValuePair<string, string?>(p.Host, p.Nickname),
            })
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);

        // get slots
        var slots = queueItems
            .Prepend(request is { Start: 0, Limit: > 0 } ? inProgressQueueItem : null)
            .Where(queueItem => queueItem != null)
            .Select((queueItem, index) =>
            {
                var isInProgress = queueItem == inProgressQueueItem;
                var percentage = (isInProgress ? progressPercentage : 0)!.Value;
                var status = isInProgress ? "Downloading" : "Queued";
                var providerUsage = ProviderUsageTracker.ToDisplayHosts(
                    providerUsageTracker.Snapshot(queueItem!.Id), configuredProviders);
                return GetQueueResponse.QueueSlot.FromQueueItem(queueItem!, index, percentage, status, providerUsage, nicknamesByHost);
            })
            .ToList();

        // return response
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Paused = false,
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(httpContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}
