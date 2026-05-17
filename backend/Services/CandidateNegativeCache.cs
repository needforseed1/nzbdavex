using System.Collections.Concurrent;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>
/// Short-lived "recently failed" cache of NZB URLs.
/// Prevents repeatedly hammering a dead release across concurrent play clicks.
/// </summary>
public class CandidateNegativeCache
{
    private readonly ConfigManager _configManager;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failedAt = new();

    public CandidateNegativeCache(ConfigManager configManager)
    {
        _configManager = configManager;
    }

    public bool IsFailed(string nzbUrl)
    {
        if (!_failedAt.TryGetValue(nzbUrl, out var failedAt)) return false;
        var ttl = _configManager.GetPlayCandidateNegativeCacheTtl();
        if (DateTimeOffset.UtcNow - failedAt < ttl) return true;
        _failedAt.TryRemove(nzbUrl, out _);
        return false;
    }

    public void MarkFailed(string nzbUrl)
    {
        _failedAt[nzbUrl] = DateTimeOffset.UtcNow;
        if (_failedAt.Count > 512) Cleanup();
    }

    private void Cleanup()
    {
        var ttl = _configManager.GetPlayCandidateNegativeCacheTtl();
        var cutoff = DateTimeOffset.UtcNow - ttl;
        foreach (var kv in _failedAt)
            if (kv.Value < cutoff) _failedAt.TryRemove(kv.Key, out _);
    }
}
