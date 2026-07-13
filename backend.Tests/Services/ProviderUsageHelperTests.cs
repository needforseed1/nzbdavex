using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Tests.Services;

public class ProviderUsageHelperTests
{
    [Fact]
    public void LegacyHostFallbackIsAllowedForUniqueHost()
    {
        var provider = Provider("provider-1", "news.unique.example");

        var eligible = ProviderUsageHelper.GetLegacyHostFallbackProviderIds([provider]);

        Assert.Contains(provider.Id, eligible);
    }

    [Fact]
    public void LegacyHostFallbackIsRejectedForSharedHost()
    {
        var providers = new[]
        {
            Provider("provider-1", "news.shared.example"),
            Provider("provider-2", "NEWS.SHARED.EXAMPLE"),
            Provider("provider-3", "news.unique.example"),
        };

        var eligible = ProviderUsageHelper.GetLegacyHostFallbackProviderIds(providers);

        Assert.DoesNotContain(providers[0].Id, eligible);
        Assert.DoesNotContain(providers[1].Id, eligible);
        Assert.Contains(providers[2].Id, eligible);
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
