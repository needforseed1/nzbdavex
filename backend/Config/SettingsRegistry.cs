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
      "usenet.providers":"","usenet.max-download-connections":"15","usenet.playback-reserved-connections":"",
      "usenet.max-queue-connections":"","usenet.warm-validation-concurrency":"",
      "usenet.ready-connections.primary":"5","usenet.ready-connections.health":"10","usenet.streaming-priority":"80",
      "usenet.read-ahead-mb":"32","usenet.segment-cache.enabled":"false","usenet.segment-cache.path":"/config/segment-cache",
      "usenet.segment-cache.max-gb":"10","usenet.pipelining.playback.enabled":"false","usenet.pipelining.health.enabled":"true",
      "usenet.pipelining.health.provider-qualification.enabled":"true",
      "usenet.pipelining.health.depth":"32","usenet.pipelining.health.lanes":"64",
      "usenet.pipelining.depth":"8","usenet.cascade.enabled":"false",
      "webdav.user":"admin","webdav.pass":"",
      "webdav.show-hidden-files":"false","webdav.enforce-readonly":"true","webdav.preview-par2-files":"false",
      "rclone.rc-enabled":"false","rclone.host":"","rclone.user":"","rclone.pass":"","rclone.mount-dir":"/mnt/nzbdav",
      "plex.enabled":"false","plex.base-url":"","plex.token":"","plex.path-prefix":"","plex.local-path-prefix":"",
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
      "maintenance.remove-orphaned-schedule-time":"0","api.nzb-backup-enabled":"false","api.nzb-backup-location":""
    }
    """;

    public static IReadOnlyDictionary<string, string> Defaults { get; } =
        JsonSerializer.Deserialize<Dictionary<string, string>>(DefaultsJson)!;

    private static readonly IReadOnlyDictionary<string, string> InternalDefaults =
        new Dictionary<string, string>
        {
            ["api.strm-key"] = "",
            ["api.lazy-rar-parsing"] = "true",
            // Accepted for older clients and used as a runtime fallback until
            // the replacement MiB setting has been saved.
            ["usenet.article-buffer-size"] = "40",
        };

    internal static readonly IReadOnlyDictionary<string, (long Min, long Max)> Ranges =
        new Dictionary<string, (long Min, long Max)>
    {
        ["usenet.max-download-connections"] = (1, int.MaxValue),
        ["usenet.playback-reserved-connections"] = (0, int.MaxValue),
        ["usenet.max-queue-connections"] = (1, int.MaxValue),
        ["usenet.warm-validation-concurrency"] = (1, int.MaxValue),
        ["usenet.ready-connections.primary"] = (0, int.MaxValue),
        ["usenet.ready-connections.health"] = (0, int.MaxValue),
        ["usenet.streaming-priority"] = (0, 100),
        ["usenet.read-ahead-mb"] = (1, 1024),
        ["usenet.article-buffer-size"] = (1, int.MaxValue),
        ["usenet.segment-cache.max-gb"] = (1, long.MaxValue / (1024L * 1024L * 1024L)),
        ["usenet.pipelining.depth"] = (1, UsenetProviderConfig.MaximumPipeliningDepth),
        ["usenet.pipelining.health.depth"] = (1, UsenetProviderConfig.MaximumPipeliningDepth),
        ["usenet.pipelining.health.lanes"] = (1, 1024),
        ["play.total-budget-seconds"] = (3, 180),
        ["play.hedge-delay-seconds"] = (1, 30),
        ["play.max-candidates"] = (1, 10),
        ["play.max-attempts"] = (1, 200),
        ["play.verify-sample-count"] = (1, 10),
        ["play.candidate-negative-cache-minutes"] = (1, 1440),
        ["grab.stall-failover-window-seconds"] = (2, 60),
        ["grab.stall-failover-ceiling-seconds"] = (5, 120),
        ["variants.tolerance-pct"] = (0, 100),
        ["variants.max-per-group"] = (0, 50),
        ["variants.eviction-active-grace-seconds"] = (0, 300),
        ["preflight.max-attempts"] = (1, 50),
        ["preflight.verify-sample-count"] = (1, 10),
        ["preflight.ttl-seconds"] = (10, 1800),
        ["preflight.indexer-max-wait-seconds"] = (0, 120),
        ["maintenance.remove-orphaned-schedule-time"] = (0, 1439),
    };

    internal static readonly IReadOnlyDictionary<string, string[]> Choices =
        new Dictionary<string, string[]>
    {
        ["api.duplicate-nzb-behavior"] = ["increment", "mark-failed"],
        ["api.import-strategy"] = ["symlinks", "strm"],
        ["play.verify-mode"] = ["none", "stat", "body"],
        ["variants.mode"] = ["off", "smart", "collect-all"],
        ["variants.replay-strategy"] = ["closest-to-click", "largest", "smallest"],
        ["variants.eviction-strategy"] = ["lru", "largest-first", "smallest-first", "never"],
        ["preflight.mode"] = ["off", "light", "standard", "full"],
    };

    internal static bool TryGetValidationDefault(string key, out string defaultValue)
    {
        if (Defaults.TryGetValue(key, out defaultValue!)) return true;
        return InternalDefaults.TryGetValue(key, out defaultValue!);
    }
}
