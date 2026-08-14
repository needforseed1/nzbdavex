using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ReleaseIdentityTests
{
    [Fact]
    public void MetadataIdentityIsStableAcrossPresentationDifferences()
    {
        var morning = DateTimeOffset.Parse("2026-08-14T01:00:00Z");
        var evening = DateTimeOffset.Parse("2026-08-14T23:00:00Z");

        var first = ReleaseIdentity.Key(1_000, " Poster ", morning, "https://one.invalid/item");
        var second = ReleaseIdentity.Key(1_000, "poster", evening, "https://two.invalid/item");

        Assert.Equal(first, second);
        Assert.StartsWith("rk1:", first);
    }

    [Fact]
    public void UrlIdentityRemainsDeterministicWithoutReleaseMetadata()
    {
        var first = ReleaseIdentity.Key(0, null, null, "https://one.invalid/item");
        var repeated = ReleaseIdentity.Key(0, null, null, "https://one.invalid/item");
        var different = ReleaseIdentity.Key(0, null, null, "https://two.invalid/item");

        Assert.Equal(first, repeated);
        Assert.NotEqual(first, different);
        Assert.StartsWith("rk1:", first);
    }
}
