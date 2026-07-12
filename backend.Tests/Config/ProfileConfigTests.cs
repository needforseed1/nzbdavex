using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Config;

public class ProfileConfigTests
{
    [Fact]
    public void NullAdaptersPreserveLegacyAllEnabledBehavior()
    {
        var profile = Profile(enabledAdapters: null);

        Assert.True(profile.IsAdapterEnabled("addon"));
        Assert.True(profile.IsAdapterEnabled("newznab"));
    }

    [Fact]
    public void EmptyAdaptersDisableEveryAdapter()
    {
        var profile = Profile([]);

        Assert.False(profile.IsAdapterEnabled("addon"));
        Assert.False(profile.IsAdapterEnabled("newznab"));
    }

    [Fact]
    public void ExplicitAdaptersAreMatchedCaseInsensitively()
    {
        var profile = Profile(["Newznab"]);

        Assert.True(profile.IsAdapterEnabled("newznab"));
        Assert.False(profile.IsAdapterEnabled("json"));
    }

    [Fact]
    public void NormalizationRepairsNullIndexerList()
    {
        var profile = Profile(null);
        profile.IndexerIds = null!;
        var config = new ProfileConfig { Profiles = [profile] };

        config.Normalized();

        Assert.Empty(profile.IndexerIds);
    }

    private static ProfileConfig.Profile Profile(List<string>? enabledAdapters) => new()
    {
        Token = "token",
        Name = "profile",
        EnabledAdapters = enabledAdapters,
    };
}
