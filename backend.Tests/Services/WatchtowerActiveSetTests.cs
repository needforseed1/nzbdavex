using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class WatchtowerActiveSetTests
{
    [Fact]
    public void LruSelectionMakesRoomAndProtectsRecentlyAccessedItems()
    {
        var oldest = Item(accessed: 10, updated: 100);
        var middle = Item(accessed: 20, updated: 100);
        var newest = Item(accessed: 30, updated: 100);

        var victims = WatchtowerService.SelectLruVictims([newest, oldest, middle], cap: 3, incoming: 2);

        Assert.Equal([oldest.Id, middle.Id], victims.Select(x => x.Id));
    }

    [Fact]
    public void LruSelectionFallsBackToVerificationResolutionAndUpdateTimes()
    {
        var oldest = Item(accessed: null, updated: 10, verified: null, resolved: null);
        var newer = Item(accessed: null, updated: 100, verified: 50, resolved: 20);

        var victim = Assert.Single(WatchtowerService.SelectLruVictims([newer, oldest], cap: 1, incoming: 0));

        Assert.Equal(oldest.Id, victim.Id);
    }

    private static WantedItem Item(long? accessed, long updated, long? verified = null, long? resolved = null) => new()
    {
        Id = Guid.NewGuid(), Key = Guid.NewGuid().ToString(), Type = "movie", ContentId = "tt1",
        Title = "item", State = WantedItem.StateReady, CreatedAtUnix = 1, UpdatedAtUnix = updated,
        LastAccessedAtUnix = accessed, LastVerifiedAtUnix = verified, LastResolvedAtUnix = resolved,
    };
}
