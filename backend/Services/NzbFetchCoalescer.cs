using System.Collections.Concurrent;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public class NzbFetchCoalescer(ConfigManager configManager)
{
    // Individual fetchers apply the configured per-indexer timeout (up to one
    // hour). Keep only a slightly wider last-resort cap here so coalescing does
    // not silently truncate an otherwise valid timeout setting.
    private static readonly TimeSpan HardFetchCap = TimeSpan.FromMinutes(65);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private DateTimeOffset _lastSweep = DateTimeOffset.UtcNow;

    public async Task<byte[]?> GetOrFetchAsync(
        string url,
        Func<CancellationToken, Task<byte[]?>> fetch,
        CancellationToken ct)
    {
        if (TryGetCached(url, out var cached)) return cached;

        var lazy = _inFlight.GetOrAdd(url, u => new Lazy<Task<byte[]?>>(() => RunSharedAsync(u, fetch)));
        return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    // Path-specific admission must happen before this caller becomes the owner
    // of the URL-global shared fetch. A rejected short wait (for example,
    // Preflight's bounded RPM wait) therefore cannot publish a shared null that
    // makes an unrelated playback/proxy caller fail. Existing cached/in-flight
    // work is joined without consuming another admission slot.
    public async Task<byte[]?> GetOrFetchAsync(
        string url,
        Func<CancellationToken, Task<bool>> admit,
        Func<CancellationToken, Task<byte[]?>> fetch,
        CancellationToken ct)
    {
        if (TryGetCached(url, out var cached)) return cached;
        if (_inFlight.TryGetValue(url, out var existing))
            return await existing.Value.WaitAsync(ct).ConfigureAwait(false);
        if (!await admit(ct).ConfigureAwait(false)) return null;
        return await GetOrFetchAsync(url, fetch, ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> RunSharedAsync(string url, Func<CancellationToken, Task<byte[]?>> fetch)
    {
        try
        {
            using var cts = new CancellationTokenSource(HardFetchCap);
            var bytes = await fetch(cts.Token).ConfigureAwait(false);
            if (bytes is not null)
            {
                var ttl = TimeSpan.FromSeconds(configManager.GetPreflightTtlSeconds());
                _cache[url] = new Entry(bytes, DateTimeOffset.UtcNow + ttl);
                SweepIfDue();
            }
            return bytes;
        }
        finally
        {
            _inFlight.TryRemove(url, out _);
        }
    }

    private bool TryGetCached(string url, out byte[]? bytes)
    {
        bytes = null;
        if (!_cache.TryGetValue(url, out var e)) return false;
        if (DateTimeOffset.UtcNow > e.ExpiresAt)
        {
            _cache.TryRemove(url, out _);
            return false;
        }
        bytes = e.Bytes;
        return true;
    }

    private void SweepIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSweep < SweepInterval) return;
        _lastSweep = now;
        foreach (var kv in _cache)
            if (now > kv.Value.ExpiresAt) _cache.TryRemove(kv.Key, out _);
    }

    private readonly record struct Entry(byte[] Bytes, DateTimeOffset ExpiresAt);
}
