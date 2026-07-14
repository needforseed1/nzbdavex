using System.Text;

namespace NzbWebDAV.Config;

public enum SettingDataType
{
    String,
    Boolean,
    Integer,
    Compound,
}

public enum SettingApplyPolicy
{
    Immediate,
    ComponentReload,
    RestartRequired,
}

public sealed record SettingDefinition(
    string Key,
    IReadOnlyList<string> YamlPath,
    string Section,
    string Subsection,
    int Order,
    SettingDataType DataType,
    string DefaultValue,
    bool Sensitive = false,
    string? EnvironmentFallback = null,
    SettingApplyPolicy ApplyPolicy = SettingApplyPolicy.Immediate,
    string? Description = null);

/// <summary>
/// Backend-owned, versioned ordering and serialization schema shared by the
/// settings API and settings.yaml. Persisted ConfigItems remain unchanged.
/// </summary>
public static class SettingsSchema
{
    public const int Version = 1;

    private static readonly string[] GuiOrder =
    [
        // Usenet
        "usenet.providers", "usenet.max-download-connections", "usenet.max-queue-connections",
        "usenet.streaming-priority", "usenet.article-buffer-size", "usenet.segment-cache.enabled",
        "usenet.segment-cache.path", "usenet.segment-cache.max-gb", "usenet.cascade.enabled",
        "usenet.pipelining.playback.enabled", "usenet.pipelining.depth",
        "usenet.pipelining.health.enabled", "usenet.pipelining.health.depth",
        "usenet.pipelining.health.lanes",

        // Indexers and Search Profiles
        "indexers.instances", "search.exclude-patterns", "api.search-user-agent", "api.user-agent",
        "profiles.instances", "general.base-url",

        // Watchdog
        "play.watchdog-enabled", "play.total-budget-seconds", "play.hedge-delay-seconds",
        "play.max-candidates", "play.max-attempts", "play.verify-mode", "play.verify-sample-count",
        "play.candidate-negative-cache-minutes", "grab.stall-failover-enabled",
        "grab.stall-failover-window-seconds", "grab.stall-failover-ceiling-seconds",
        "variants.mode", "variants.tolerance-pct", "variants.max-per-group",
        "variants.replay-strategy", "variants.fallback-on-failure", "variants.eviction-strategy",
        "variants.eviction-active-grace-seconds",

        // Preflight
        "preflight.mode", "preflight.max-attempts", "preflight.verify-sample-count",
        "preflight.ttl-seconds", "preflight.indexer-max-wait-seconds",

        // Watchtower
        "watchtower.enabled", "watchtower.profile-token", "watchtower.ranking",
        "watchtower.size-floor-bytes", "watchtower.size-ceiling-bytes", "watchtower.shortlist-depth",
        "watchtower.grab-cap-per-resolve", "watchtower.active-set-cap",
        "watchtower.daily-resolve-budget", "watchtower.auto-throughput",
        "watchtower.sync-interval-seconds", "watchtower.series-scope", "watchtower.season-bundles",
        "watchtower.series-max-episodes", "watchtower.series-cap-keep",
        "watchtower.series-recent-count", "watchtower.season-bundle-fallback",
        "watchtower.season-bundle-fallback-scope",
        "watchtower.season-bundle-fallback-recent-count",
        "watchtower.season-bundle-fallback-max-episodes", "watchtower.min-grabs",
        "watchtower.verify-sample-count", "watchtower.verify-timeout-seconds",
        "watchtower.keepfresh-base-seconds", "watchtower.keepfresh-max-seconds",
        "watchtower.unavailable-retry-seconds", "watchtower.verbose-logging",

        // Warden
        "warden.hide-dead", "warden.quorum", "warden.backbone-scope",

        // Advanced: WebDAV
        "webdav.user", "webdav.pass", "webdav.enforce-readonly", "webdav.show-hidden-files",
        "webdav.preview-par2-files",

        // Advanced: SABnzbd
        "api.key", "api.categories", "api.manual-category", "api.import-strategy", "rclone.mount-dir",
        "api.completed-downloads-dir", "api.download-file-blocklist", "api.duplicate-nzb-behavior",
        "api.ensure-importable-video", "api.ensure-article-existence-categories",
        "api.ignore-history-limit", "api.nzb-backup-enabled", "api.nzb-backup-location",

        // Advanced: Radarr/Sonarr, Repairs, Rclone, Maintenance
        "arr.instances", "repair.enable", "media.library-dir", "rclone.rc-enabled", "rclone.host",
        "rclone.user", "rclone.pass", "db.is-startup-vacuum-enabled",
        "maintenance.remove-orphaned-schedule-enabled", "maintenance.remove-orphaned-schedule-time",
    ];

    public static IReadOnlyList<SettingDefinition> Ordered { get; } = Build();

    public static IReadOnlyDictionary<string, SettingDefinition> ByKey { get; } =
        Ordered.ToDictionary(x => x.Key, StringComparer.Ordinal);

    private static IReadOnlyList<SettingDefinition> Build()
    {
        var definitions = GuiOrder.Select((key, order) => Create(key, order)).ToArray();
        var duplicates = definitions.GroupBy(x => x.Key).Where(x => x.Count() != 1).Select(x => x.Key).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException($"Settings schema contains duplicate keys: {string.Join(", ", duplicates)}");
        var duplicatePaths = definitions.GroupBy(x => string.Join('.', x.YamlPath), StringComparer.Ordinal)
            .Where(x => x.Count() != 1).Select(x => x.Key).ToArray();
        if (duplicatePaths.Length > 0)
            throw new InvalidOperationException(
                $"Settings schema contains duplicate YAML paths: {string.Join(", ", duplicatePaths)}");

        var missing = SettingsRegistry.Defaults.Keys.Except(definitions.Select(x => x.Key), StringComparer.Ordinal).ToArray();
        var extra = definitions.Select(x => x.Key).Except(SettingsRegistry.Defaults.Keys, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
            throw new InvalidOperationException(
                $"Settings schema mismatch. Missing: {string.Join(", ", missing)}. Extra: {string.Join(", ", extra)}.");
        return definitions;
    }

    private static SettingDefinition Create(string key, int order)
    {
        var defaultValue = SettingsRegistry.Defaults[key];
        var dataType = key is "usenet.providers" or "indexers.instances" or "profiles.instances" or "arr.instances"
            ? SettingDataType.Compound
            : defaultValue is "true" or "false"
                ? SettingDataType.Boolean
                : long.TryParse(defaultValue, out _) || key == "usenet.max-queue-connections"
                    ? SettingDataType.Integer
                    : SettingDataType.String;
        var (section, subsection, path) = Location(key);
        return new SettingDefinition(
            key, path, section, subsection, order, dataType, defaultValue,
            IsSensitive(key), EnvironmentFallback(key), ApplyPolicy(key), Description(key));
    }

    private static (string Section, string Subsection, string[] Path) Location(string key)
    {
        if (key == "usenet.providers") return ("usenet", "providers", ["usenet", "providers"]);
        if (key.StartsWith("usenet.max-") || key == "usenet.streaming-priority")
            return ("usenet", "concurrency_and_scheduling",
                ["usenet", "advanced", "concurrency_and_scheduling", Leaf(key, "usenet.")]);
        if (key is "usenet.article-buffer-size" || key.StartsWith("usenet.segment-cache."))
            return ("usenet", "streaming_and_cache",
                ["usenet", "advanced", "streaming_and_cache", Leaf(key, "usenet.")]);
        if (key == "usenet.cascade.enabled")
            return ("usenet", "provider_routing", ["usenet", "advanced", "provider_routing", "cascade_enabled"]);
        if (key.StartsWith("usenet.pipelining."))
            return ("usenet", "pipelining", ["usenet", "advanced", "pipelining", Leaf(key, "usenet.pipelining.")]);

        if (key == "indexers.instances") return ("indexers", "definitions", ["indexers", "configuration"]);
        if (key is "search.exclude-patterns" or "api.search-user-agent" or "api.user-agent")
            return ("indexers", "advanced", ["indexers", Leaf(key, key.StartsWith("search.") ? "search." : "api.")]);
        if (key == "profiles.instances") return ("search_profiles", "definitions", ["search_profiles", "definitions"]);
        if (key == "general.base-url") return ("search_profiles", "general", ["search_profiles", "base_url"]);

        if (key.StartsWith("play."))
            return ("watchdog", "playback", ["watchdog", "playback", Leaf(key, "play.")]);
        if (key.StartsWith("grab."))
            return ("watchdog", "grab_failover", ["watchdog", "grab_failover", Leaf(key, "grab.")]);
        if (key.StartsWith("variants."))
            return ("watchdog", "variants", ["watchdog", "variants", Leaf(key, "variants.")]);
        if (key.StartsWith("preflight.")) return ("preflight", "general", ["preflight", Leaf(key, "preflight.")]);
        if (key.StartsWith("watchtower.")) return ("watchtower", "general", ["watchtower", Leaf(key, "watchtower.")]);
        if (key.StartsWith("warden.")) return ("warden", "behavior", ["warden", "behavior", Leaf(key, "warden.")]);

        if (key.StartsWith("webdav."))
            return ("advanced", "webdav", ["advanced", "webdav", key == "webdav.pass" ? "password_hash" : Leaf(key, "webdav.")]);
        if (key == "arr.instances")
            return ("advanced", "radarr_sonarr", ["advanced", "radarr_sonarr", "configuration"]);
        if (key.StartsWith("repair.") || key == "media.library-dir")
            return ("advanced", "repairs", ["advanced", "repairs", Leaf(key, key.StartsWith("repair.") ? "repair." : "media.")]);
        if (key.StartsWith("rclone.") && key != "rclone.mount-dir")
            return ("advanced", "rclone_server", ["advanced", "rclone_server", Leaf(key, "rclone.")]);
        if (key.StartsWith("db.") || key.StartsWith("maintenance."))
            return ("advanced", "maintenance", ["advanced", "maintenance", Leaf(key, key.StartsWith("db.") ? "db." : "maintenance.")]);

        // Remaining API settings and rclone.mount-dir are displayed in SABnzbd.
        return ("advanced", "sabnzbd", ["advanced", "sabnzbd", Leaf(key, key.StartsWith("api.") ? "api." : "rclone.")]);
    }

    private static string Leaf(string key, string prefix) => SnakeCase(key[prefix.Length..].Replace('.', '_'));

    private static string SnakeCase(string value)
    {
        var result = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '-' or '.') result.Append('_');
            else if (char.IsUpper(c))
            {
                if (result.Length > 0 && result[^1] != '_') result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else result.Append(c);
        }
        return result.ToString();
    }

    private static bool IsSensitive(string key) => key is
        "usenet.providers" or "indexers.instances" or "profiles.instances" or "arr.instances"
        or "webdav.pass" or "rclone.pass" or "api.key" or "watchtower.profile-token";

    private static string? EnvironmentFallback(string key) => key switch
    {
        "rclone.mount-dir" => "MOUNT_DIR",
        "api.categories" => "CATEGORIES",
        "webdav.user" => "WEBDAV_USER",
        "webdav.pass" => "WEBDAV_PASSWORD",
        "api.user-agent" => "NZB_GRAB_USER_AGENT",
        "api.search-user-agent" => "NZB_SEARCH_USER_AGENT",
        "api.key" => "FRONTEND_BACKEND_API_KEY",
        _ => null,
    };

    private static SettingApplyPolicy ApplyPolicy(string key) => key switch
    {
        "db.is-startup-vacuum-enabled" => SettingApplyPolicy.RestartRequired,
        "rclone.mount-dir" => SettingApplyPolicy.RestartRequired,
        "usenet.providers" or "usenet.segment-cache.enabled" or "usenet.segment-cache.path"
            or "usenet.segment-cache.max-gb" => SettingApplyPolicy.ComponentReload,
        _ => SettingApplyPolicy.Immediate,
    };

    private static string Description(string key) => key switch
    {
        "usenet.max-queue-connections" => "null means automatic pool sizing",
        "webdav.pass" => "one-way WebDAV password hash; plaintext password is accepted on import only",
        _ => key.Replace('.', ' ').Replace('-', ' '),
    };
}
