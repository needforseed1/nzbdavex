using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ActiveReadRegistryTests
{
    [Fact]
    public void PruneExpired_KeepsASessionWhoseRequestIsStillOpen()
    {
        var (registry, clock) = CreateRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        clock.Advance(TimeSpan.FromMinutes(1));

        // A player that filled its buffer and paused stops consuming bytes and
        // stops provoking fetches, but its request is still open: everything it
        // does next belongs to this session. Persisting a terminal row here
        // throws away every stall, byte and provider fact that follows.
        Assert.Empty(registry.PruneExpired());
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void PruneExpired_TakesTheSessionOnceItsLastRequestEnds()
    {
        var (registry, clock) = CreateRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        registry.MarkRequestEnded(id, ReadSession.EndReasonCode.Completed);
        clock.Advance(TimeSpan.FromMinutes(1));

        var pruned = Assert.Single(registry.PruneExpired());
        Assert.Equal(id, pruned.Id);
        Assert.Equal(0, registry.Count);
        Assert.Empty(registry.DrainAll());
    }

    [Fact]
    public void PruneExpired_KeepsASessionUntilEveryConcurrentRequestHasEnded()
    {
        var (registry, clock) = CreateRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        // Players commonly run several ranges of the same file at once.
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        registry.MarkRequestEnded(id, ReadSession.EndReasonCode.Aborted);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(registry.PruneExpired());

        registry.MarkRequestEnded(id, ReadSession.EndReasonCode.Completed);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Single(registry.PruneExpired());
    }

    [Fact]
    public void PruneExpired_NeverReclaimsARequestThatIsStillOpen()
    {
        var (registry, clock) = CreateRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Empty(registry.PruneExpired());
        Assert.Single(registry.Snapshot());
    }

    [Fact]
    public void Snapshot_IsStableWhileTheLiveSessionKeepsChanging()
    {
        var (registry, clock) = CreateRegistry();
        var id = registry.GetOrCreate(
            "/content/movie",
            "10.0.0.5|vlc",
            "movie.mkv",
            fileSize: 1_000);
        registry.Touch(id, bytesRead: 100, currentOffset: 200);
        var before = Assert.Single(registry.Snapshot());

        clock.Advance(TimeSpan.FromSeconds(1));
        registry.Touch(id, bytesRead: 50, currentOffset: 300);
        var after = Assert.Single(registry.Snapshot());

        Assert.Equal(100, before.BytesRead);
        Assert.Equal(200, before.CurrentOffset);
        Assert.Equal(150, after.BytesRead);
        Assert.Equal(300, after.CurrentOffset);
        Assert.Equal(300, after.MaxOffset);
    }

    [Fact]
    public void ReopeningAfterPrune_CreatesAFreshSessionId()
    {
        var (registry, clock) = CreateRegistry();
        var first = registry.GetOrCreate(
            "/content/movie",
            "10.0.0.5|vlc",
            "movie.mkv",
            fileSize: null);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(registry.PruneExpired());

        var second = registry.GetOrCreate(
            "/content/movie",
            "10.0.0.5|vlc",
            "movie.mkv",
            fileSize: null);

        Assert.NotEqual(first, second);
    }

    private static (ActiveReadRegistry Registry, TestClock Clock) CreateRegistry()
    {
        var clock = new TestClock();
        return (new ActiveReadRegistry(() => clock.Now), clock);
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } =
            new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan elapsed) => Now += elapsed;
    }
}
