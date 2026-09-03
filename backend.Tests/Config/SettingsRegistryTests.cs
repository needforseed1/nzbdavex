using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Config;

public class SettingsRegistryTests
{
    [Fact]
    public void RegistryDescribesValidationRules()
    {
        Assert.Equal("false", SettingsRegistry.Defaults["usenet.segment-cache.enabled"]);
        Assert.Equal("true", SettingsRegistry.Defaults[
            "usenet.pipelining.health.provider-qualification.enabled"]);
        Assert.Equal("", SettingsRegistry.Defaults["usenet.playback-reserved-connections"]);
        Assert.Equal("32", SettingsRegistry.Defaults["usenet.read-ahead-mb"]);
        Assert.Equal("50", SettingsRegistry.Defaults["api.nzb-backup-retention-count"]);
        Assert.Equal((0, int.MaxValue), SettingsRegistry.Ranges["usenet.playback-reserved-connections"]);
        Assert.Equal((1, 1024), SettingsRegistry.Ranges["usenet.read-ahead-mb"]);
        Assert.Equal((1, int.MaxValue), SettingsRegistry.Ranges["api.nzb-backup-retention-count"]);
        Assert.True(SettingsRegistry.TryGetValidationDefault("usenet.article-buffer-size", out _));
        Assert.Equal((1, 64), SettingsRegistry.Ranges["usenet.pipelining.depth"]);
        Assert.Equal((1, 64), SettingsRegistry.Ranges["usenet.pipelining.health.depth"]);
        Assert.Contains("strm", SettingsRegistry.Choices["api.import-strategy"]);
    }

    [Theory]
    [InlineData("watchtower.enabled")]
    [InlineData("watchtower.resolve-concurrency")]
    [InlineData("warden.hide-dead")]
    [InlineData("warden.max-source-entries")]
    public void RetiredFeatureSettingsAreNotWritable(string key)
    {
        Assert.DoesNotContain(key, SettingsRegistry.Defaults.Keys);
        Assert.False(SettingsRegistry.TryGetValidationDefault(key, out _));
    }

    [Fact]
    public void EveryNumericSettingHasAnExplicitRange()
    {
        var numericKeys = SettingsRegistry.Defaults
            .Where(x => long.TryParse(x.Value, out _))
            .Select(x => x.Key)
            .Append("usenet.max-queue-connections")
            .Append("usenet.playback-reserved-connections");

        foreach (var key in numericKeys)
            Assert.True(SettingsRegistry.Ranges.ContainsKey(key), $"Missing range for {key}");
    }

    [Fact]
    public void EveryRangeAndChoiceBelongsToAWritableSetting()
    {
        foreach (var key in SettingsRegistry.Ranges.Keys.Concat(SettingsRegistry.Choices.Keys))
            Assert.True(SettingsRegistry.TryGetValidationDefault(key, out _), $"Unknown validation key {key}");
    }
}
