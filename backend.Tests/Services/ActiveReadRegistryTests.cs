using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ActiveReadRegistryTests
{
    [Fact]
    public void PruneExpired_KeepsASessionWhoseRequestIsStillOpen()
    {
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        Expire(registry, id);

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
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        registry.MarkRequestEnded(id, ReadSession.EndReasonCode.Completed);
        Expire(registry, id);

        var pruned = Assert.Single(registry.PruneExpired());
        Assert.Equal(id, pruned.Id);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void PruneExpired_KeepsASessionUntilEveryConcurrentRequestHasEnded()
    {
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        // Players commonly run several ranges of the same file at once.
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        registry.MarkRequestEnded(id, ReadSession.EndReasonCode.Aborted);
        Expire(registry, id);

        Assert.Empty(registry.PruneExpired());

        registry.MarkRequestEnded(id, ReadSession.EndReasonCode.Completed);
        Expire(registry, id);

        Assert.Single(registry.PruneExpired());
    }

    [Fact]
    public void PruneExpired_NeverReclaimsARequestThatIsStillOpen()
    {
        var registry = new ActiveReadRegistry();
        var id = registry.GetOrCreate("/content/movie", "10.0.0.5|vlc", "movie.mkv", fileSize: null);
        registry.MarkRequestStarted(id, "10.0.0.5", "vlc");
        Expire(registry, id, TimeSpan.FromHours(1));

        Assert.Empty(registry.PruneExpired());
        Assert.Single(registry.Snapshot());
    }

    private static void Expire(ActiveReadRegistry registry, Guid id, TimeSpan? age = null)
    {
        var entry = registry.Snapshot().Single(x => x.Id == id);
        entry.LastActivityAt = DateTimeOffset.UtcNow - (age ?? TimeSpan.FromMinutes(1));
    }
}
