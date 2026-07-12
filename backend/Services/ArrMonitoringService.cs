using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// - This class takes care of monitoring Radarr/Sonarr instances
///   for stuck queue items which usually require manual intervention.
/// - NzbDAV can be configured to automatically remove these stuck items,
///   optionally block these stuck items, and optionally trigger a new
///   search for these stuck items.
/// </summary>
public class ArrMonitoringService : BackgroundService
{
    private readonly ConfigManager _configManager;

    public ArrMonitoringService(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Ensure delay runs on each iteration
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);

            // if all queue-actions are disabled, then do nothing
            var arrConfig = _configManager.GetArrConfig();
            if (arrConfig.QueueRules.All(x => x.Action == ArrConfig.QueueAction.DoNothing))
                continue;

            // otherwise, handle stuck queue items according to the config
            await Task.WhenAll(arrConfig.GetArrClients()
                .Select(arrClient => HandleStuckQueueItems(arrConfig, arrClient, stoppingToken)))
                .ConfigureAwait(false);
        }
    }

    private async Task HandleStuckQueueItems(
        ArrConfig arrConfig, ArrClient client, CancellationToken ct)
    {
        try
        {
            var queueStatus = await client.GetQueueStatusAsync(ct).ConfigureAwait(false);
            if (queueStatus is { Warnings: false, UnknownWarnings: false }) return;
            var queue = await client.GetQueueAsync(ct).ConfigureAwait(false);
            var actionableStatuses = arrConfig.QueueRules.Select(x => x.Message);
            var stuckRecords = queue.Records.Where(x => actionableStatuses.Any(x.HasStatusMessage));
            foreach (var record in stuckRecords)
                await HandleStuckQueueItem(record, arrConfig, client, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException { InnerException: System.Net.Sockets.SocketException })
        {
            Log.Debug($"Could not reach Arr instance `{client.Host}` for queue monitoring: {e.Message}");
        }
        catch (Exception e)
        {
            Log.Error($"Error occured while monitoring queue for `{client.Host}`: {e.Message}");
        }
    }

    private async Task HandleStuckQueueItem(
        ArrQueueRecord item, ArrConfig arrConfig, ArrClient client, CancellationToken ct)
    {
        // since there may be multiple status messages, multiple actions may apply.
        // in such case, always perform the strongest action.
        var action = arrConfig.QueueRules
            .Where(x => item.HasStatusMessage(x.Message))
            .Select(x => x.Action)
            .DefaultIfEmpty(ArrConfig.QueueAction.DoNothing)
            .Max();

        if (action is ArrConfig.QueueAction.DoNothing) return;
        var status = await client.DeleteQueueRecord(item.Id, action, ct).ConfigureAwait(false);
        if ((int)status is < 200 or >= 300)
        {
            Log.Warning("Arr queue action {Action} for {Title} on {Host} failed with HTTP {Status}",
                action, item.Title, client.Host, (int)status);
            return;
        }
        Log.Warning("Resolved stuck queue item {Title} from {Host} with action {Action}",
            item.Title, client.Host, action);
    }
}
