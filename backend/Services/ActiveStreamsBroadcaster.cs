using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Ticks once per second to publish the current set of active WebDAV streams
/// plus their per-backbone segment counts over the websocket. When no streams
/// are active, the loop is mostly idle (just a sleep + a Count check). Sends
/// nothing when nothing has changed since the last broadcast.
/// </summary>
public class ActiveStreamsBroadcaster(
    ActiveStreamRegistry registry,
    ProviderUsageTracker usageTracker,
    WebsocketManager websocketManager
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string? _lastPayload;
    private bool _wasEmpty = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await BroadcastTickAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Debug(e, "ActiveStreamsBroadcaster tick failed");
            }
        }
    }

    private async Task BroadcastTickAsync()
    {
        // Prune sessions that haven't been touched in the activity window first,
        // so their counters don't leak in the tracker.
        var pruned = registry.PruneExpired();
        foreach (var id in pruned) usageTracker.Clear(id);

        var entries = registry.Snapshot();

        // Common case: nothing active, nothing was active. Skip serialization entirely.
        if (entries.Count == 0 && _wasEmpty) return;

        var usage = usageTracker.SnapshotMany(entries.Select(e => e.Id));
        var snapshot = new
        {
            streams = entries.Select(e => new
            {
                id = e.Id,
                fileName = e.FileName,
                path = e.Path,
                startedAt = e.StartedAt.ToUnixTimeMilliseconds(),
                lastActivityAt = e.LastActivityAt.ToUnixTimeMilliseconds(),
                bytesRead = Interlocked.Read(ref e.BytesRead),
                fileSize = e.FileSize,
                providers = (usage.GetValueOrDefault(e.Id) ?? new Dictionary<string, long>())
                    .Select(kv => new { host = kv.Key, segments = kv.Value })
                    .OrderByDescending(p => p.segments)
                    .ToList()
            }).ToList()
        };

        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (payload == _lastPayload) return;
        _lastPayload = payload;
        _wasEmpty = entries.Count == 0;
        await websocketManager.SendMessage(WebsocketTopic.ActiveStreams, payload).ConfigureAwait(false);
    }
}
