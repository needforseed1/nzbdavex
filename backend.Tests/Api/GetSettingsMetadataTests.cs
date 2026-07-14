using NzbWebDAV.Api.Controllers.GetSettingsMetadata;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Api;

public class GetSettingsMetadataTests
{
    [Fact]
    public void StoredBlanksUseRuntimeDefaultsUnlessBlankHasMeaning()
    {
        var config = WithValues(
            ("play.total-budget-seconds", ""),
            ("indexers.instances", ""),
            ("usenet.max-queue-connections", ""),
            ("general.base-url", ""));

        Assert.Equal("30", GetSettingsMetadataController.ResolveEffectiveValue(
            "play.total-budget-seconds", "", config));
        Assert.Equal(SettingsRegistry.Defaults["indexers.instances"],
            GetSettingsMetadataController.ResolveEffectiveValue("indexers.instances", "", config));
        Assert.Equal("", GetSettingsMetadataController.ResolveEffectiveValue(
            "usenet.max-queue-connections", "", config));
        Assert.Equal("", GetSettingsMetadataController.ResolveEffectiveValue(
            "general.base-url", "", config));
    }

    [Theory]
    [InlineData("webdav.pass")]
    [InlineData("rclone.pass")]
    public void PasswordValuesAreAlwaysMasked(string key)
    {
        Assert.Equal("", GetSettingsMetadataController.ResolveEffectiveValue(
            key, "stored-secret", new ConfigManager()));
    }

    private static ConfigManager WithValues(params (string Key, string Value)[] values)
    {
        var config = new ConfigManager();
        config.UpdateValues(values.Select(x => new ConfigItem
        {
            ConfigName = x.Key,
            ConfigValue = x.Value,
        }).ToList());
        return config;
    }
}
