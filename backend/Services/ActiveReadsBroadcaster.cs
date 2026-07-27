using Microsoft.Extensions.Hosting;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Ticks once per second to publish the current set of active WebDAV read
/// sessions plus their per-backbone segment counts over the websocket. When no
/// sessions are active, the loop is mostly idle (just a sleep + a Count check).
/// Sends nothing when nothing has changed since the last broadcast.
/// </summary>
public class ActiveReadsBroadcaster(
    ActiveReadRegistry registry,
    WebsocketManager websocketManager,
    PlaybackSessionStats playbackSessionStats,
    ActiveReadsSnapshotBuilder snapshotBuilder,
    PlaybackSessionRecorder sessionRecorder
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StatsStaleAfter = TimeSpan.FromMinutes(10);
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
                Log.Debug(e, "ActiveReadsBroadcaster tick failed");
            }
        }
    }

    private async Task BroadcastTickAsync()
    {
        // Prune sessions that haven't been touched in the activity window first,
        // so their counters don't leak in the tracker. Each pruned entry becomes
        // a terminal ReadSession row so the dashboard can show historical reads.
        var pruned = registry.PruneExpired();
        foreach (var entry in pruned)
        {
            sessionRecorder.Record(entry);
            snapshotBuilder.Forget(entry.Id);
        }

        var entries = registry.Snapshot();
        // Accumulators whose session never reached the prune path would otherwise
        // linger for the process lifetime. Never age out a request that is still
        // registered: a quiet ten-minute stretch in a long movie is not a stale
        // diagnostic session.
        playbackSessionStats.DropStale(
            StatsStaleAfter,
            entries.Select(entry => entry.Id).ToHashSet());

        // Common case: nothing active, nothing was active. Skip serialization entirely.
        if (entries.Count == 0 && _wasEmpty) return;

        var payload = snapshotBuilder.BuildPayload(entries, DateTimeOffset.UtcNow);
        if (payload == _lastPayload) return;
        _lastPayload = payload;
        _wasEmpty = entries.Count == 0;
        await websocketManager.SendMessage(WebsocketTopic.ActiveReads, payload).ConfigureAwait(false);
    }

    /// <summary>
    /// On shutdown the tick loop stops before the activity window expires, so
    /// anything still streaming would never reach the recorder. Flush it.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var entry in registry.DrainAll()) sessionRecorder.Record(entry);
        }
        catch (Exception e)
        {
            Log.Debug(e, "ActiveReadsBroadcaster shutdown flush failed");
        }
    }
}
