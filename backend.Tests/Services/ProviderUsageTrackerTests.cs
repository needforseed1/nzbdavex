using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class ProviderUsageTrackerTests
{
    [Fact]
    public void ToDisplayHosts_ReplacesStableIdsWithConfiguredHosts()
    {
        var providers = new[]
        {
            Provider("aaa3cc10-c54e-35d4-2079-c173b6d71879", "news.eweka.nl"),
            Provider("516d64b0-d4a3-4ba5-11c1-fb2455486143", "news.usenet.farm"),
        };
        var usage = new Dictionary<string, long>
        {
            [providers[0].Id] = 164,
            [providers[1].Id] = 162,
        };

        var display = ProviderUsageTracker.ToDisplayHosts(usage, providers);

        Assert.Equal(164, display["news.eweka.nl"]);
        Assert.Equal(162, display["news.usenet.farm"]);
        Assert.DoesNotContain(providers[0].Id, display.Keys);
        Assert.DoesNotContain(providers[1].Id, display.Keys);
    }

    [Fact]
    public void ToDisplayHosts_PreservesLegacyHostsAndAggregatesSharedHosts()
    {
        var providers = new[] { Provider("provider-id", "news.example.com") };
        var usage = new Dictionary<string, long>
        {
            ["provider-id"] = 10,
            ["news.example.com"] = 5,
            ["unknown-provider"] = 2,
        };

        var display = ProviderUsageTracker.ToDisplayHosts(usage, providers);

        Assert.Equal(15, display["news.example.com"]);
        Assert.Equal(2, display["unknown-provider"]);
    }

    private static UsenetProviderConfig.ConnectionDetails Provider(string id, string host) => new()
    {
        Id = id,
        Type = ProviderType.Pooled,
        Host = host,
        Port = 563,
        UseSsl = true,
        User = "user",
        Pass = "pass",
        MaxConnections = 10,
    };
}
