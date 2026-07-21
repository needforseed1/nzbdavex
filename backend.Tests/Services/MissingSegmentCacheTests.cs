using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class MissingSegmentCacheTests
{
    [Fact]
    public void EntriesExpireAfterTtl()
    {
        var clock = new FakeTimeProvider();
        var cache = new HealthCheckService.MissingSegmentCache(clock);

        cache.Add("segment-1");
        Assert.True(cache.Contains("segment-1"));

        clock.Advance(HealthCheckService.MissingSegmentCache.Ttl - TimeSpan.FromSeconds(1));
        Assert.True(cache.Contains("segment-1"));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.Contains("segment-1"));
        // The expired entry was removed lazily during the lookup.
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ClearRemovesEverythingImmediately()
    {
        var clock = new FakeTimeProvider();
        var cache = new HealthCheckService.MissingSegmentCache(clock);
        cache.Add("segment-1");
        cache.Add("segment-2");

        cache.Clear();

        Assert.False(cache.Contains("segment-1"));
        Assert.False(cache.Contains("segment-2"));
    }

    [Fact]
    public void ReAddingRefreshesTheExpiry()
    {
        var clock = new FakeTimeProvider();
        var cache = new HealthCheckService.MissingSegmentCache(clock);

        cache.Add("segment-1");
        clock.Advance(HealthCheckService.MissingSegmentCache.Ttl - TimeSpan.FromSeconds(1));
        cache.Add("segment-1");
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(cache.Contains("segment-1"));
    }

    [Fact]
    public void CompactionDropsExpiredEntriesFirstThenEnforcesTheCap()
    {
        var clock = new FakeTimeProvider();
        var cache = new HealthCheckService.MissingSegmentCache(clock);

        // Half the cap expires before the overflow insertion.
        for (var i = 0; i < HealthCheckService.MissingSegmentCache.MaxEntries / 2; i++)
            cache.Add($"old-{i}");
        clock.Advance(HealthCheckService.MissingSegmentCache.Ttl + TimeSpan.FromSeconds(1));
        for (var i = 0; i < HealthCheckService.MissingSegmentCache.MaxEntries / 2; i++)
            cache.Add($"new-{i}");

        // This insertion pushes the raw count over the cap and triggers
        // compaction, which must remove the expired half rather than any of
        // the live entries.
        cache.Add("overflow");

        Assert.True(cache.Count <= HealthCheckService.MissingSegmentCache.MaxEntries);
        Assert.True(cache.Contains("overflow"));
        Assert.True(cache.Contains("new-0"));
        Assert.False(cache.Contains("old-0"));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _utcNow += by;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
