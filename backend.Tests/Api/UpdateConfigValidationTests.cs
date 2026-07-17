using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api;

public class UpdateConfigValidationTests
{
    [Theory]
    [InlineData("latest-season")]
    [InlineData("first-season")]
    [InlineData("all-aired")]
    [InlineData("recent")]
    [InlineData("off")]
    public void AcceptsEveryWatchtowerSeriesScopeUsedByTheUiAndRuntime(string value)
    {
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = "watchtower.series-scope", ConfigValue = value },
        ]);
    }

    [Fact]
    public void RejectsObsoleteWatchtowerSeriesScopeAlias()
    {
        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = "watchtower.series-scope", ConfigValue = "all" },
            ]));
    }

    [Theory]
    [InlineData("latest-season")]
    [InlineData("all")]
    [InlineData("recent")]
    public void AcceptsEverySeasonBundleFallbackScopeUsedByTheUiAndRuntime(string value)
    {
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = "watchtower.season-bundle-fallback-scope", ConfigValue = value },
        ]);
    }

    [Fact]
    public void QueueConnectionLimitAcceptsAutomaticButRejectsInvalidText()
    {
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = "usenet.max-queue-connections", ConfigValue = "" },
        ]);

        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = "usenet.max-queue-connections", ConfigValue = "invalid" },
            ]));
    }

    [Fact]
    public void WarmValidationConnectionsAcceptAutomaticAndEnforceGlobalRange()
    {
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = "usenet.warm-validation-concurrency", ConfigValue = "" },
        ]);
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = "usenet.warm-validation-concurrency", ConfigValue = "512" },
        ]);

        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = "usenet.warm-validation-concurrency", ConfigValue = "513" },
            ]));
    }

    [Theory]
    [InlineData("usenet.ready-connections.primary")]
    [InlineData("usenet.ready-connections.health")]
    public void ReadyConnectionFloorsAcceptZeroAndEnforceGlobalRange(string key)
    {
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = key, ConfigValue = "0" },
        ]);
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = key, ConfigValue = "512" },
        ]);

        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = key, ConfigValue = "513" },
            ]));
    }

    [Theory]
    [InlineData("play.total-budget-seconds", "1")]
    [InlineData("preflight.max-attempts", "0")]
    [InlineData("watchtower.size-floor-bytes", "-1")]
    [InlineData("maintenance.remove-orphaned-schedule-time", "1440")]
    [InlineData("variants.max-per-group", "51")]
    public void RejectsValuesOutsideRuntimeAndUiRanges(string key, string value)
    {
        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = key, ConfigValue = value },
            ]));
    }

    [Fact]
    public void RejectsUnknownKeysButAcceptsIntentionalInternalSettings()
    {
        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = "play.total-buget-seconds", ConfigValue = "30" },
            ]));

        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = "api.strm-key", ConfigValue = "internal-key" },
            new ConfigItem { ConfigName = "api.lazy-rar-parsing", ConfigValue = "true" },
            new ConfigItem { ConfigName = "watchtower.resolve-concurrency", ConfigValue = "3" },
            new ConfigItem { ConfigName = "warden.max-source-entries", ConfigValue = "2000000" },
        ]);
    }

    [Fact]
    public void AcceptsDotNetSpecificSearchRegex()
    {
        ConfigUpdateValidator.Validate([
            new ConfigItem { ConfigName = "search.exclude-patterns", ConfigValue = "(?-i:Foo)" },
        ]);
    }

    [Fact]
    public void RejectsInvalidSearchRegexBeforePersistence()
    {
        var exception = Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = "search.exclude-patterns", ConfigValue = "[unterminated" },
            ]));

        Assert.Contains("line 1", exception.Message);
    }

    [Fact]
    public void RejectsProviderValuesThatCouldBreakPoolConstruction()
    {
        var exception = Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem
                {
                    ConfigName = "usenet.providers",
                    ConfigValue = """
                        {"Providers":[{"Id":"11111111-1111-1111-1111-111111111111","Type":1,"Host":"news.example","Port":70000,"UseSsl":true,"User":"u","Pass":"p","MaxConnections":0}]}
                        """,
                },
            ]));

        Assert.Contains("ports", exception.Message);
    }

    [Fact]
    public void RejectsCaseInsensitiveDuplicateIndexerNames()
    {
        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem
                {
                    ConfigName = "indexers.instances",
                    ConfigValue = """
                        {"Indexers":[
                          {"Id":"11111111-1111-1111-1111-111111111111","Name":"Main","Url":"https://one.example","ApiKey":"a"},
                          {"Id":"22222222-2222-2222-2222-222222222222","Name":"main","Url":"https://two.example","ApiKey":"b"}
                        ]}
                        """,
                },
            ]));
    }

    [Theory]
    [InlineData("usenet.providers", "{\"Providers\":[null]}")]
    [InlineData("indexers.instances", "{\"Indexers\":[null]}")]
    [InlineData("profiles.instances", "{\"Profiles\":[null]}")]
    [InlineData("arr.instances", "{\"RadarrInstances\":[null],\"SonarrInstances\":[],\"QueueRules\":[]}")]
    public void RejectsNullRowsInEmbeddedModels(string key, string value)
    {
        Assert.Throws<BadHttpRequestException>(() =>
            ConfigUpdateValidator.Validate([
                new ConfigItem { ConfigName = key, ConfigValue = value },
            ]));
    }
}
