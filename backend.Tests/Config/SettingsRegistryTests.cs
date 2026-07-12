using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Config;

public class SettingsRegistryTests
{
    [Fact]
    public void RegistryDescribesTypesRangesSecretsAndRestartRequirements()
    {
        Assert.Equal("boolean", SettingsRegistry.Describe("usenet.segment-cache.enabled").Type);
        Assert.True(SettingsRegistry.Describe("usenet.segment-cache.enabled").RestartRequired);
        Assert.True(SettingsRegistry.Describe("usenet.providers").Secret);
        Assert.Equal(1, SettingsRegistry.Describe("watchtower.active-set-cap").Min);
        Assert.Contains("strm", SettingsRegistry.Describe("api.import-strategy").Choices!);
    }
}
