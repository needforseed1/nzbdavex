using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services.Metrics;

/// <summary>
/// Publishes the "live" Overview tile snapshot every five seconds: active read
/// count, articles fetched per minute, error count, and bytes served. Reads are
/// taken from the registry (in-memory, instant) and rolling counters are
/// computed from the raw event tables over a sliding 60 s window. The payload
/// only fires when something has changed.
/// </summary>
public class LiveStatsBroadcaster(
    ActiveReadRegistry registry,
    WebsocketManager websocketManager
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string? _lastPayload;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                await BroadcastAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "LiveStatsBroadcaster tick failed");
            }
        }
    }

    private async Task BroadcastAsync()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sinceMs = nowMs - 60_000;

        await using var db = new MetricsDbContext();
        var articles = await db.SegmentFetches
            .Where(x => x.At >= sinceMs)
            .CountAsync().ConfigureAwait(false);
        var errors = await db.SegmentFetches
            .Where(x => x.At >= sinceMs && x.Status != SegmentFetch.FetchStatus.Ok)
            .CountAsync().ConfigureAwait(false);
        var bytesServed = await db.ReadSessions
            .Where(x => x.EndedAt >= sinceMs)
            .SumAsync(x => (long?)x.BytesServed).ConfigureAwait(false) ?? 0L;

        var snapshot = new
        {
            activeReads = registry.Count,
            articlesPerMinute = articles,
            errorsPerMinute = errors,
            bytesServedPerMinute = bytesServed,
            ts = nowMs,
        };

        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        // Skip resends when nothing has moved (ignoring the ts field so idle
        // ticks don't churn the socket). Cheap approximation: compare body
        // without ts by stripping the trailing field.
        if (PayloadBodyEquals(payload, _lastPayload)) return;
        _lastPayload = payload;
        await websocketManager.SendMessage(WebsocketTopic.LiveStats, payload).ConfigureAwait(false);
    }

    private static bool PayloadBodyEquals(string a, string? b)
    {
        if (b == null) return false;
        var aEnd = a.LastIndexOf(",\"ts\":", StringComparison.Ordinal);
        var bEnd = b.LastIndexOf(",\"ts\":", StringComparison.Ordinal);
        if (aEnd < 0 || bEnd < 0) return a == b;
        return a.AsSpan(0, aEnd).SequenceEqual(b.AsSpan(0, bEnd));
    }
}
