using NzbWebDAV.Config;

namespace NzbWebDAV.Tests.Config;

public class SettingsRegistryTests
{
    [Fact]
    public void RegistryDescribesValidationRules()
    {
        Assert.Equal("false", SettingsRegistry.Defaults["usenet.segment-cache.enabled"]);
        Assert.Equal((1, 100000), SettingsRegistry.Ranges["watchtower.active-set-cap"]);
        Assert.Contains("strm", SettingsRegistry.Choices["api.import-strategy"]);
    }

    [Fact]
    public void WatchtowerSeriesScopeChoicesMatchRuntimeValues()
    {
        string[] expected = ["latest-season", "first-season", "all-aired", "recent", "off"];

        Assert.Equal(expected, SettingsRegistry.Choices["watchtower.series-scope"]);
        foreach (var value in expected)
            Assert.Equal(value, ConfigManager.NormalizeSeriesScope(value));

        Assert.Null(ConfigManager.NormalizeSeriesScope("all"));
    }

    [Fact]
    public void EveryNumericSettingHasAnExplicitRange()
    {
        var numericKeys = SettingsRegistry.Defaults
            .Where(x => long.TryParse(x.Value, out _))
            .Select(x => x.Key)
            .Append("usenet.max-queue-connections");

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
