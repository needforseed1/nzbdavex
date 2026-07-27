using System.Text.Json;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>
/// Maps immutable registry sessions and their live telemetry into the active-read
/// websocket contract. It also owns the previous-tick byte samples needed for
/// per-session transfer rates.
/// </summary>
public sealed class ActiveReadsSnapshotBuilder(
    ProviderUsageTracker usageTracker,
    ConfigManager configManager,
    PlaybackSessionStats playbackSessionStats)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Dictionary<Guid, ByteSample> _lastBytes = new();

    public string BuildPayload(
        IReadOnlyList<ActiveReadSessionSnapshot> entries,
        DateTimeOffset now)
    {
        var usage = usageTracker.SnapshotMany(entries.Select(entry => entry.Id));
        var providersByIdentity = configManager.GetUsenetProviderConfig().Providers
            .ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        var reads = entries
            .Select(entry => BuildRead(
                entry,
                usage.GetValueOrDefault(entry.Id),
                providersByIdentity,
                now))
            .ToList();

        return JsonSerializer.Serialize(new ActiveReadsSnapshot(reads), JsonOptions);
    }

    public void Forget(Guid sessionId) => _lastBytes.Remove(sessionId);

    private ActiveReadSnapshot BuildRead(
        ActiveReadSessionSnapshot entry,
        IReadOnlyDictionary<string, long>? usage,
        IReadOnlyDictionary<string, UsenetProviderConfig.ConnectionDetails> providersByIdentity,
        DateTimeOffset now)
    {
        var live = playbackSessionStats.Peek(entry.Id);
        var providers = (usage ?? new Dictionary<string, long>())
            .Select(item =>
            {
                providersByIdentity.TryGetValue(item.Key, out var configured);
                return new ActiveReadProviderSnapshot(
                    configured?.Host ?? item.Key,
                    configured?.Nickname,
                    item.Value);
            })
            .OrderByDescending(provider => provider.Segments)
            .ToList();

        return new ActiveReadSnapshot(
            entry.Id,
            entry.FileName,
            entry.Path,
            entry.StartedAt.ToUnixTimeMilliseconds(),
            entry.LastActivityAt.ToUnixTimeMilliseconds(),
            entry.BytesRead,
            entry.CurrentOffset,
            entry.FileSize,
            RateSince(entry.Id, entry.BytesRead, now),
            live?.UpstreamStalls ?? 0,
            live?.TotalUpstreamStallMs ?? 0,
            live?.ActiveUpstreamWaits ?? 0,
            live?.DownstreamStalls ?? 0,
            live?.ZeroFilledSegments ?? 0,
            live?.BodyStallRecoveries ?? 0,
            providers);
    }

    /// <summary>
    /// Bytes per second since the previous tick. Returns zero for a read seen
    /// for the first time because no interval has been observed yet.
    /// </summary>
    private long RateSince(Guid id, long bytes, DateTimeOffset now)
    {
        if (!_lastBytes.TryGetValue(id, out var previous))
        {
            _lastBytes[id] = new ByteSample(bytes, now);
            return 0;
        }

        var elapsed = (now - previous.At).TotalSeconds;
        _lastBytes[id] = new ByteSample(bytes, now);
        if (elapsed <= 0) return 0;
        return (long)Math.Max(0, (bytes - previous.Bytes) / elapsed);
    }

    private readonly record struct ByteSample(long Bytes, DateTimeOffset At);

    private sealed record ActiveReadsSnapshot(IReadOnlyList<ActiveReadSnapshot> Reads);

    private sealed record ActiveReadSnapshot(
        Guid Id,
        string FileName,
        string Path,
        long StartedAt,
        long LastActivityAt,
        long BytesRead,
        long CurrentOffset,
        long? FileSize,
        long BytesPerSecond,
        int UpstreamStalls,
        long TotalUpstreamStallMs,
        int UpstreamWaitsInProgress,
        int DownstreamStalls,
        int ZeroFilledSegments,
        int BodyStallRecoveries,
        IReadOnlyList<ActiveReadProviderSnapshot> Providers);

    private sealed record ActiveReadProviderSnapshot(
        string Host,
        string? Nickname,
        long Segments);
}
