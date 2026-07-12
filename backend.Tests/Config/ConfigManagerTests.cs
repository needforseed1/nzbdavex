using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class ConfigManagerTests
{
    [Fact]
    public void InvalidScalarValuesFallBackWithoutThrowing()
    {
        var config = WithValues(
            ("api.ensure-importable-video", "not-a-boolean"),
            ("usenet.max-download-connections", "not-an-integer"),
            ("usenet.article-buffer-size", "not-an-integer"),
            ("usenet.segment-cache.max-gb", "not-a-long"),
            ("usenet.streaming-priority", "not-an-integer"));

        Assert.True(config.IsEnsureImportableVideoEnabled());
        Assert.Equal(1, config.GetMaxDownloadConnections());
        Assert.Equal(40, config.GetArticleBufferSize());
        Assert.Equal(10L * 1024 * 1024 * 1024, config.GetSegmentCacheMaxBytes());
        Assert.Equal(80, config.GetStreamingPriority().HighPriorityOdds);
    }

    [Fact]
    public void ScalarValuesAreClampedToSafeRanges()
    {
        var config = WithValues(
            ("usenet.max-download-connections", "-5"),
            ("usenet.article-buffer-size", "0"),
            ("usenet.segment-cache.max-gb", long.MaxValue.ToString()),
            ("usenet.streaming-priority", "500"));

        Assert.Equal(1, config.GetMaxDownloadConnections());
        Assert.Equal(1, config.GetArticleBufferSize());
        Assert.True(config.GetSegmentCacheMaxBytes() > 0);
        Assert.Equal(100, config.GetStreamingPriority().HighPriorityOdds);
    }

    [Fact]
    public void InvalidAndNullCollectionJsonFallsBackToEmptyConfigs()
    {
        var invalid = WithValues(
            ("arr.instances", "{"),
            ("usenet.providers", "{"),
            ("indexers.instances", "{"),
            ("profiles.instances", "{"));

        Assert.Empty(invalid.GetArrConfig().RadarrInstances);
        Assert.Empty(invalid.GetUsenetProviderConfig().Providers);
        Assert.Empty(invalid.GetIndexerConfig().Indexers);
        Assert.Empty(invalid.GetProfileConfig().Profiles);

        var nullCollections = WithValues(
            ("arr.instances", "{\"RadarrInstances\":null,\"SonarrInstances\":null,\"QueueRules\":null}"),
            ("usenet.providers", "{\"Providers\":null}"),
            ("indexers.instances", "{\"Indexers\":null}"),
            ("profiles.instances", "{\"Profiles\":null}"));

        Assert.Empty(nullCollections.GetArrConfig().RadarrInstances);
        Assert.Empty(nullCollections.GetUsenetProviderConfig().Providers);
        Assert.Empty(nullCollections.GetIndexerConfig().Indexers);
        Assert.Empty(nullCollections.GetProfileConfig().Profiles);
    }

    [Fact]
    public void EnumLikeSettingsAreTrimmedAndCaseNormalized()
    {
        var config = WithValues(
            ("api.duplicate-nzb-behavior", " MARK-FAILED "),
            ("api.import-strategy", " STRM "),
            ("play.verify-mode", " BODY "),
            ("variants.mode", " COLLECT-ALL "),
            ("preflight.mode", " STANDARD "));

        Assert.Equal("mark-failed", config.GetDuplicateNzbBehavior());
        Assert.Equal("strm", config.GetImportStrategy());
        Assert.Equal("body", config.GetPlayVerifyMode());
        Assert.Equal("collect-all", config.GetVariantsMode());
        Assert.Equal("standard", config.GetPreflightMode());
    }

    [Fact]
    public void BlankPathsUseDefaultsAndFilesystemRootIsPreserved()
    {
        var config = WithValues(
            ("api.completed-downloads-dir", "   "),
            ("usenet.segment-cache.path", "   "),
            ("rclone.mount-dir", "/"));

        Assert.Equal("/data/completed-downloads", config.GetStrmCompletedDownloadDir());
        Assert.Equal("/config/segment-cache", config.GetSegmentCachePath());
        Assert.Equal("/", config.GetRcloneMountDir());
    }

    [Fact]
    public void RcloneEndpointAndUserAreNormalizedConsistently()
    {
        var config = WithValues(
            ("rclone.host", "  http://rclone:5572///  "),
            ("rclone.user", "  operator  "));

        Assert.Equal("http://rclone:5572", config.GetRcloneHost());
        Assert.Equal("operator", config.GetRcloneUser());
    }

    [Fact]
    public void WatchtowerKeepFreshMaximumCannotBeBelowBase()
    {
        var config = WithValues(
            ("watchtower.keepfresh-base-seconds", "7200"),
            ("watchtower.keepfresh-max-seconds", "600"));

        Assert.Equal(7200, config.GetWatchtowerKeepFreshMaxSeconds());
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
