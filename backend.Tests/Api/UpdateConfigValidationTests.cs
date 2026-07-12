using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Controllers.UpdateConfig;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api;

public class UpdateConfigValidationTests
{
    [Fact]
    public void AcceptsDotNetSpecificSearchRegex()
    {
        UpdateConfigController.ValidateConfigItems([
            new ConfigItem { ConfigName = "search.exclude-patterns", ConfigValue = "(?-i:Foo)" },
        ]);
    }

    [Fact]
    public void RejectsInvalidSearchRegexBeforePersistence()
    {
        var exception = Assert.Throws<BadHttpRequestException>(() =>
            UpdateConfigController.ValidateConfigItems([
                new ConfigItem { ConfigName = "search.exclude-patterns", ConfigValue = "[unterminated" },
            ]));

        Assert.Contains("line 1", exception.Message);
    }

    [Fact]
    public void RejectsProviderValuesThatCouldBreakPoolConstruction()
    {
        var exception = Assert.Throws<BadHttpRequestException>(() =>
            UpdateConfigController.ValidateConfigItems([
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
            UpdateConfigController.ValidateConfigItems([
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
            UpdateConfigController.ValidateConfigItems([
                new ConfigItem { ConfigName = key, ConfigValue = value },
            ]));
    }
}
