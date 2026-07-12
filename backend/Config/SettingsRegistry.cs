using System.Text.Json;

namespace NzbWebDAV.Config;

public static class SettingsRegistry
{
    private const string DefaultsJson = """
    {
      "general.base-url":"","api.key":"","api.categories":"","api.manual-category":"uncategorized",
      "api.ensure-importable-video":"true","api.ensure-article-existence-categories":"","api.ignore-history-limit":"true",
      "api.download-file-blocklist":"*.nfo, *.par2, *.sfv, *sample.mkv","api.duplicate-nzb-behavior":"increment",
      "api.import-strategy":"symlinks","api.completed-downloads-dir":"/data/completed-downloads","api.user-agent":"","api.search-user-agent":"",
      "usenet.providers":"","usenet.max-download-connections":"15","usenet.max-queue-connections":"","usenet.streaming-priority":"80",
      "usenet.article-buffer-size":"40","usenet.segment-cache.enabled":"false","usenet.segment-cache.path":"/config/segment-cache",
      "usenet.segment-cache.max-gb":"10","usenet.pipelining.playback.enabled":"false","usenet.pipelining.health.enabled":"true",
      "usenet.pipelining.health.depth":"32","usenet.pipelining.health.lanes":"64",
      "usenet.pipelining.depth":"8","usenet.cascade.enabled":"false","webdav.user":"admin","webdav.pass":"",
      "webdav.show-hidden-files":"false","webdav.enforce-readonly":"true","webdav.preview-par2-files":"false",
      "rclone.rc-enabled":"false","rclone.host":"","rclone.user":"","rclone.pass":"","rclone.mount-dir":"/mnt/nzbdav",
      "media.library-dir":"","arr.instances":"{\"RadarrInstances\":[],\"SonarrInstances\":[],\"QueueRules\":[]}",
      "indexers.instances":"{\"Indexers\":[]}","profiles.instances":"{\"Profiles\":[]}",
      "play.watchdog-enabled":"true","play.total-budget-seconds":"30","play.hedge-delay-seconds":"3","play.max-candidates":"3",
      "play.max-attempts":"10","play.verify-mode":"none","play.verify-sample-count":"3","play.candidate-negative-cache-minutes":"5",
      "grab.stall-failover-enabled":"true","grab.stall-failover-window-seconds":"2","grab.stall-failover-ceiling-seconds":"5",
      "search.exclude-patterns":"","variants.mode":"off","variants.tolerance-pct":"25","variants.max-per-group":"3",
      "variants.replay-strategy":"closest-to-click","variants.fallback-on-failure":"true","variants.eviction-strategy":"lru",
      "variants.eviction-active-grace-seconds":"60","preflight.mode":"off","preflight.max-attempts":"20",
      "preflight.verify-sample-count":"3","preflight.ttl-seconds":"120","preflight.indexer-max-wait-seconds":"5",
      "repair.enable":"false","db.is-startup-vacuum-enabled":"false","maintenance.remove-orphaned-schedule-enabled":"false",
      "maintenance.remove-orphaned-schedule-time":"0","api.nzb-backup-enabled":"false","api.nzb-backup-location":"",
      "watchtower.enabled":"false","watchtower.profile-token":"","watchtower.ranking":"watchdog",
      "watchtower.size-floor-bytes":"536870912","watchtower.size-ceiling-bytes":"0","watchtower.shortlist-depth":"2",
      "watchtower.grab-cap-per-resolve":"3","watchtower.active-set-cap":"100","watchtower.daily-resolve-budget":"60",
      "watchtower.auto-throughput":"false","watchtower.sync-interval-seconds":"3600","watchtower.series-scope":"latest-season",
      "watchtower.season-bundles":"true","watchtower.series-max-episodes":"50","watchtower.series-cap-keep":"newest",
      "watchtower.series-recent-count":"3","watchtower.season-bundle-fallback":"false",
      "watchtower.season-bundle-fallback-scope":"latest-season","watchtower.season-bundle-fallback-recent-count":"2",
      "watchtower.season-bundle-fallback-max-episodes":"50","watchtower.min-grabs":"0","watchtower.verify-sample-count":"3",
      "watchtower.verify-timeout-seconds":"10","watchtower.keepfresh-base-seconds":"21600","watchtower.keepfresh-max-seconds":"604800",
      "watchtower.unavailable-retry-seconds":"21600","watchtower.verbose-logging":"false","warden.hide-dead":"true",
      "warden.quorum":"2","warden.backbone-scope":"true"
    }
    """;

    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        JsonSerializer.Deserialize<Dictionary<string, string>>(DefaultsJson)!;

    private static readonly HashSet<string> Secrets =
        ["webdav.pass", "rclone.pass", "api.key", "usenet.providers", "indexers.instances", "arr.instances"];

    private static readonly HashSet<string> RestartRequired =
        ["usenet.segment-cache.enabled", "usenet.segment-cache.path", "usenet.segment-cache.max-gb", "db.is-startup-vacuum-enabled"];

    private static readonly Dictionary<string, (long Min, long Max)> Ranges = new()
    {
        ["usenet.streaming-priority"] = (0, 100),
        ["usenet.pipelining.depth"] = (1, 64),
        ["usenet.pipelining.health.depth"] = (1, 64),
        ["usenet.pipelining.health.lanes"] = (1, 1024),
        ["watchtower.active-set-cap"] = (1, 100000),
        ["watchtower.verify-sample-count"] = (1, 20),
        ["watchtower.verify-timeout-seconds"] = (2, 120),
        ["warden.quorum"] = (1, 20),
    };

    private static readonly Dictionary<string, string[]> Choices = new()
    {
        ["api.duplicate-nzb-behavior"] = ["increment", "mark-failed"],
        ["api.import-strategy"] = ["symlinks", "strm"],
        ["play.verify-mode"] = ["none", "stat", "body"],
        ["variants.mode"] = ["off", "smart", "collect-all"],
        ["variants.replay-strategy"] = ["closest-to-click", "largest", "smallest"],
        ["variants.eviction-strategy"] = ["lru", "largest-first", "smallest-first", "never"],
        ["preflight.mode"] = ["off", "light", "standard", "full"],
        ["watchtower.ranking"] = ["watchdog", "largest"],
        ["watchtower.series-scope"] = ["latest-season", "first-season", "all", "recent"],
        ["watchtower.series-cap-keep"] = ["newest", "oldest"],
    };

    public static SettingDescriptor Describe(string key)
    {
        var defaultValue = Defaults[key];
        var type = defaultValue is "true" or "false" ? "boolean"
            : Choices.ContainsKey(key) ? "choice"
            : key.EndsWith(".instances", StringComparison.Ordinal) || key == "usenet.providers" ? "json"
            : long.TryParse(defaultValue, out _) ? "integer"
            : "string";
        Ranges.TryGetValue(key, out var range);
        return new SettingDescriptor(
            key, defaultValue, type, Secrets.Contains(key), RestartRequired.Contains(key),
            Ranges.ContainsKey(key) ? range.Min : null,
            Ranges.ContainsKey(key) ? range.Max : null,
            Choices.GetValueOrDefault(key));
    }

    public sealed record SettingDescriptor(
        string Key,
        string DefaultValue,
        string Type,
        bool Secret,
        bool RestartRequired,
        long? Min,
        long? Max,
        IReadOnlyList<string>? Choices);
}
