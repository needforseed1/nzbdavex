using System.Collections.Concurrent;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>
/// In-memory per-queue-item counter of which Usenet provider served each segment.
/// Scope is propagated via AsyncLocal so deep async calls inside QueueItemProcessor
/// pick up the current queue-item id without threading parameters through every layer.
/// </summary>
public class ProviderUsageTracker(ActiveReadRegistry? activeReadRegistry = null)
{
    private static readonly AsyncLocal<Guid?> CurrentScope = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> _usage = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, long>> _bytes = new();
    private readonly ConcurrentDictionary<Guid, byte> _byteCaptureScopes = new();
    private readonly ConcurrentDictionary<Guid, long> _failoverSaves = new();
    private readonly ConcurrentDictionary<Guid, PrepUsageSnapshot> _prepStats = new();
    private readonly ConcurrentDictionary<Guid, int> _healthCheckTotals = new();
    private readonly ConcurrentDictionary<Guid, HealthCheckOutcome> _healthCheckOutcomes = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HealthProviderStat>> _healthStats = new();

    public IDisposable BeginScope(Guid queueItemId)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = queueItemId;
        return new Releaser(() => CurrentScope.Value = previous);
    }

    public void RecordSuccess(string providerHost)
    {
        var qid = CurrentScope.Value;
        if (qid == null || string.IsNullOrEmpty(providerHost)) return;
        var counts = _usage.GetOrAdd(qid.Value, _ => new ConcurrentDictionary<string, long>());
        counts.AddOrUpdate(providerHost, 1, (_, v) => v + 1);
        // Keep the active-read entry alive while NNTP fetches are flowing for
        // this scope. No-op when the scope id isn't a registered read session.
        activeReadRegistry?.Touch(qid.Value, 0);
    }

    public void RecordFailoverSave()
    {
        var id = CurrentScope.Value;
        if (id == null) return;
        _failoverSaves.AddOrUpdate(id.Value, 1, (_, v) => v + 1);
    }

    public void RecordBytes(string providerId, int bytes)
    {
        var id = CurrentScope.Value;
        if (id is null || !_byteCaptureScopes.ContainsKey(id.Value) ||
            bytes <= 0 || string.IsNullOrWhiteSpace(providerId)) return;
        var counts = _bytes.GetOrAdd(id.Value, _ => new ConcurrentDictionary<string, long>());
        counts.AddOrUpdate(providerId, bytes, (_, value) => value + bytes);
    }

    public IDisposable BeginByteCapture()
    {
        var id = CurrentScope.Value;
        if (id is null) return new Releaser(static () => { });
        _byteCaptureScopes[id.Value] = 0;
        return new Releaser(() => _byteCaptureScopes.TryRemove(id.Value, out _));
    }

    public long GetFailoverSaves(Guid scopeId)
        => _failoverSaves.TryGetValue(scopeId, out var v) ? v : 0;

    public void RecordPrepStats(PrepUsageSnapshot stats)
    {
        var id = CurrentScope.Value;
        if (id is null) return;
        _prepStats[id.Value] = stats;
    }

    public PrepUsageSnapshot? SnapshotPrep(Guid scopeId) =>
        _prepStats.GetValueOrDefault(scopeId);

    public void BeginHealthCheck(int totalArticles)
    {
        var id = CurrentScope.Value;
        if (id is null) return;
        _healthCheckTotals[id.Value] = Math.Max(0, totalArticles);
        _healthCheckOutcomes.TryRemove(id.Value, out _);
    }

    public void CompleteHealthCheck(int? foundArticles, int missingArticles)
    {
        var id = CurrentScope.Value;
        if (id is null) return;
        _healthCheckOutcomes[id.Value] = new HealthCheckOutcome(
            foundArticles is null ? null : Math.Max(0, foundArticles.Value),
            Math.Max(0, missingArticles));
    }

    public void RecordHealthProviderStat(HealthProviderStat stat)
    {
        var id = CurrentScope.Value;
        if (id is null || string.IsNullOrWhiteSpace(stat.ProviderId)) return;
        var stats = _healthStats.GetOrAdd(id.Value, _ => new ConcurrentDictionary<string, HealthProviderStat>());
        stats[stat.ProviderId] = stat;
    }

    public HealthCheckUsageSnapshot? SnapshotHealthCheck(Guid scopeId)
    {
        if (!_healthCheckTotals.TryGetValue(scopeId, out var totalArticles)) return null;
        var providers = _healthStats.TryGetValue(scopeId, out var stats)
            ? stats.Values.OrderByDescending(x => x.Found).ThenBy(x => x.Host).ToArray()
            : [];
        var outcome = _healthCheckOutcomes.GetValueOrDefault(scopeId);
        return new HealthCheckUsageSnapshot(
            totalArticles,
            providers,
            outcome?.FoundArticles,
            outcome?.MissingArticles);
    }

    public IReadOnlyDictionary<string, long> Snapshot(Guid queueItemId)
    {
        if (!_usage.TryGetValue(queueItemId, out var d)) return new Dictionary<string, long>();
        return d.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public IReadOnlyDictionary<string, long> SnapshotBytes(Guid queueItemId)
    {
        if (!_bytes.TryGetValue(queueItemId, out var d)) return new Dictionary<string, long>();
        return d.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, long>> SnapshotMany(IEnumerable<Guid> ids)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, long>>();
        foreach (var id in ids)
        {
            if (_usage.TryGetValue(id, out var d))
                result[id] = d.ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        return result;
    }

    /// <summary>
    /// Converts stable provider IDs used by internal accounting back to configured
    /// hostnames for user-facing queue, history, and watchdog output. Older
    /// hostname-keyed snapshots and unknown providers remain readable.
    /// </summary>
    public static IReadOnlyDictionary<string, long> ToDisplayHosts(
        IReadOnlyDictionary<string, long> usage,
        IEnumerable<UsenetProviderConfig.ConnectionDetails> providers)
    {
        var hostsById = providers
            .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Host))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Host, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, count) in usage)
        {
            var host = hostsById.GetValueOrDefault(key) ?? key;
            result[host] = result.GetValueOrDefault(host) + count;
        }
        return result;
    }

    public void Clear(Guid queueItemId)
    {
        _usage.TryRemove(queueItemId, out _);
        _bytes.TryRemove(queueItemId, out _);
        _byteCaptureScopes.TryRemove(queueItemId, out _);
        _failoverSaves.TryRemove(queueItemId, out _);
        _prepStats.TryRemove(queueItemId, out _);
        _healthCheckTotals.TryRemove(queueItemId, out _);
        _healthCheckOutcomes.TryRemove(queueItemId, out _);
        _healthStats.TryRemove(queueItemId, out _);
    }

    private sealed class Releaser(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed record HealthCheckOutcome(int? FoundArticles, int MissingArticles);
}

public sealed record PrepUsageSnapshot(
    int FileCount,
    int Connections,
    long QueueWaitMs,
    long FirstSegmentsMs,
    long Par2Ms,
    long RarMs,
    long ProcessorsMs,
    bool LazyRarMounted,
    long FirstSegmentFallbacks,
    IReadOnlyList<PrepProviderStat> Providers);

public sealed record PrepProviderStat(
    string ProviderId,
    long Articles,
    long Bytes);

public sealed record HealthCheckUsageSnapshot(
    int TotalArticles,
    IReadOnlyList<HealthProviderStat> Providers,
    int? FoundArticles = null,
    int? MissingArticles = null);

public sealed record HealthProviderStat(
    string ProviderId,
    string Host,
    bool Preferred,
    int ProbeFound,
    int ProbeReceived,
    long Batches,
    long Attempted,
    long Received,
    long Found,
    long Missing,
    long Failures,
    long WorkMs,
    long Rate,
    string? ProbeStatus = null);
