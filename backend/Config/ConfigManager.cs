using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "0.0.0";

    private readonly Dictionary<string, string> _config = new();
    private readonly HashSet<string> _invalidConfigWarnings = [];
    private readonly Lazy<string?> _environmentWebdavPasswordHash = new(() =>
    {
        var password = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        return password is null ? null : PasswordUtil.Hash(password);
    });
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            _invalidConfigWarnings.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        if (rawValue == null) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(rawValue);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            var shouldLog = false;
            lock (_config)
            {
                shouldLog = _invalidConfigWarnings.Add(configName);
            }

            if (shouldLog)
                Log.Warning(e, "Ignoring invalid JSON for config setting {ConfigName}; using defaults.", configName);
            return default;
        }
    }

    private bool GetBoolean(string configName, bool defaultValue)
    {
        if (SettingsRegistry.Defaults.TryGetValue(configName, out var registered)
            && bool.TryParse(registered, out var registeredDefault)) defaultValue = registeredDefault;
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue != null && bool.TryParse(rawValue, out var value) ? value : defaultValue;
    }

    private int GetInteger(string configName, int defaultValue, int minValue, int maxValue)
    {
        if (configName != "usenet.max-download-connections"
            && SettingsRegistry.Defaults.TryGetValue(configName, out var registered)
            && int.TryParse(registered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var registeredDefault))
            defaultValue = registeredDefault;
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue != null
               && int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minValue, maxValue)
            : defaultValue;
    }

    private long GetLongInteger(string configName, long defaultValue, long minValue, long maxValue)
    {
        if (SettingsRegistry.Defaults.TryGetValue(configName, out var registered)
            && long.TryParse(registered, NumberStyles.Integer, CultureInfo.InvariantCulture, out var registeredDefault))
            defaultValue = registeredDefault;
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue != null
               && long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minValue, maxValue)
            : defaultValue;
    }

    private string GetChoice(string configName, string defaultValue, params string[] allowedValues)
    {
        if (SettingsRegistry.Defaults.TryGetValue(configName, out var registered)) defaultValue = registered;
        var value = StringUtil.EmptyToNull(GetConfigValue(configName))?.Trim().ToLowerInvariant();
        return value != null && allowedValues.Contains(value, StringComparer.Ordinal) ? value : defaultValue;
    }

    public bool IsWardenHideDeadEnabled()
    {
        return GetBoolean("warden.hide-dead", true);
    }

    public int GetWardenQuorum()
    {
        return GetInteger("warden.quorum", 2, 1, 20);
    }

    public int GetWardenMaxSourceEntries()
    {
        return GetInteger("warden.max-source-entries", 2_000_000, 1, 10_000_000);
    }

    public bool IsWardenBackboneScopeEnabled()
    {
        return GetBoolean("warden.backbone-scope", true);
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
                _invalidConfigWarnings.Remove(configItem.ConfigName);
            }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        return Path.TrimEndingDirectorySeparator(mountDir.Trim());
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.categories"))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        // PasswordHasher salts each call. Cache the environment fallback so
        // Basic-auth verification can reuse PasswordUtil's verification cache.
        return _environmentWebdavPasswordHash.Value;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        return GetBoolean("api.ensure-importable-video", true);
    }

    public bool ShowHiddenWebdavFiles()
    {
        return GetBoolean("webdav.show-hidden-files", false);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"))?.Trim();
    }

    public int GetMaxDownloadConnections()
    {
        var defaultValue = Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15);
        return GetInteger("usenet.max-download-connections", defaultValue, 1, int.MaxValue);
    }

    public int GetMaxQueueConnections()
    {
        var pool = Math.Max(1, GetUsenetProviderConfig().TotalPooledConnections);
        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.max-queue-connections"));
        if (configured is null || !int.TryParse(configured, out var value))
            return pool;
        return Math.Clamp(value, 1, pool);
    }

    public bool IsPlaybackPipeliningEnabled()
    {
        return GetBoolean("usenet.pipelining.playback.enabled", false);
    }

    public bool IsHealthPipeliningEnabled()
    {
        return GetBoolean("usenet.pipelining.health.enabled", true);
    }

    public bool IsBackupHealthChecksEnabled()
    {
        return GetBoolean("usenet.health.include-backup.enabled", false);
    }

    public bool IsCascadeEnabled()
    {
        return GetBoolean("usenet.cascade.enabled", false);
    }

    public int GetPipeliningDepth()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.pipelining.depth"));
        if (configured is null || !int.TryParse(configured, out var value)) return 8;
        return Math.Clamp(value, 1, 64);
    }

    public int GetHealthPipeliningDepth()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.pipelining.health.depth"));
        if (configured is null || !int.TryParse(configured, out var value)) return 32;
        return Math.Clamp(value, 1, 64);
    }

    public int GetHealthPipeliningLanes()
    {
        var configured = StringUtil.EmptyToNull(GetConfigValue("usenet.pipelining.health.lanes"));
        if (configured is null || !int.TryParse(configured, out var value)) return 64;
        var availableConnections = GetHealthCheckConnections();
        return Math.Clamp(value, 1, availableConnections);
    }

    public int GetHealthCheckConnections()
    {
        var includeBackup = IsBackupHealthChecksEnabled();
        return Math.Max(1, GetUsenetProviderConfig().Providers
            .Where(x => x.Type is ProviderType.Pooled
                or ProviderType.BackupAndStats
                or ProviderType.HealthChecksOnly
                || (includeBackup && x.Type == ProviderType.BackupOnly))
            .Sum(x => x.MaxConnections));
    }

    public int GetArticleBufferSize()
    {
        return GetInteger("usenet.article-buffer-size", 40, 1, int.MaxValue);
    }

    public bool IsSegmentCacheEnabled()
    {
        return GetBoolean("usenet.segment-cache.enabled", false);
    }

    public string GetSegmentCachePath()
    {
        return StringUtil.EmptyToNull(GetConfigValue("usenet.segment-cache.path"))?.Trim()
               ?? "/config/segment-cache";
    }

    public long GetSegmentCacheMaxBytes()
    {
        const long bytesPerGb = 1024L * 1024L * 1024L;
        var gb = GetLongInteger("usenet.segment-cache.max-gb", 10, 1, long.MaxValue / bytesPerGb);
        return gb * bytesPerGb;
    }

    // When true, RAR archives are mounted instantly by parsing only the first
    // volume at import; trailing volumes are resolved on first read. Falls
    // back to eager parsing for archives that don't fit the supported shape
    // (multi-file, solid, encrypted, or compressed).
    public bool IsLazyRarParsingEnabled()
    {
        return GetBoolean("api.lazy-rar-parsing", true);
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var numericalValue = GetInteger("usenet.streaming-priority", 80, 0, 100);
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        return GetBoolean("webdav.enforce-readonly", true);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPlaybackWatchdogEnabled()
    {
        return GetBoolean("play.watchdog-enabled", true);
    }

    public int GetPlayTotalBudgetSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.total-budget-seconds"));
        if (v == null) return 30;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 3, 180) : 30;
    }

    public int GetPlayHedgeDelaySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.hedge-delay-seconds"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 30) : 3;
    }

    public int GetPlayMaxCandidates()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.max-candidates"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetPlayMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.max-attempts"));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 200) : 10;
    }

    public string GetPlayVerifyMode()
    {
        return GetChoice("play.verify-mode", "none", "body", "stat", "none");
    }

    public int GetPlayVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.verify-sample-count"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public TimeSpan GetPlayCandidateNegativeCacheTtl()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.candidate-negative-cache-minutes"));
        if (v == null) return TimeSpan.FromMinutes(5);
        return int.TryParse(v, out var n) ? TimeSpan.FromMinutes(Math.Clamp(n, 1, 60 * 24)) : TimeSpan.FromMinutes(5);
    }

    public bool IsGrabStallFailoverEnabled()
    {
        return GetBoolean("grab.stall-failover-enabled", true);
    }

    public int GetGrabStallFailoverWindowSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("grab.stall-failover-window-seconds"));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 60) : 2;
    }

    public int GetGrabStallFailoverCeilingSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("grab.stall-failover-ceiling-seconds"));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 5, 120) : 5;
    }

    public IReadOnlyList<Regex> GetSearchExcludePatterns()
    {
        var raw = GetConfigValue("search.exclude-patterns");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Regex>();

        var patterns = new List<Regex>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            try
            {
                patterns.Add(new Regex(
                    trimmed,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(250)));
            }
            catch (ArgumentException e)
            {
                Log.Warning("Skipping invalid search.exclude-patterns regex {Pattern}: {Message}", trimmed, e.Message);
            }
        }
        return patterns;
    }

    public string GetVariantsMode()
    {
        return GetChoice("variants.mode", "off", "smart", "collect-all", "off");
    }

    public int GetVariantsTolerancePct()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.tolerance-pct"));
        if (v == null) return 25;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 100) : 25;
    }

    public int GetVariantsMaxPerGroup()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.max-per-group"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 3;
    }

    public string GetVariantsReplayStrategy()
    {
        return GetChoice("variants.replay-strategy", "closest-to-click",
            "largest", "smallest", "closest-to-click");
    }

    public bool IsVariantsFallbackOnFailureEnabled()
    {
        return GetBoolean("variants.fallback-on-failure", true);
    }

    public string GetVariantsEvictionStrategy()
    {
        return GetChoice("variants.eviction-strategy", "lru",
            "largest-first", "smallest-first", "never", "lru");
    }

    public int GetVariantsEvictionActiveGraceSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.eviction-active-grace-seconds"));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 300) : 60;
    }

    public string GetPreflightMode()
    {
        return GetChoice("preflight.mode", "off", "light", "standard", "full", "off");
    }

    public int GetPreflightMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.max-attempts"));
        if (v == null) return 20;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 50) : 20;
    }

    public int GetPreflightVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.verify-sample-count"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetPreflightTtlSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.ttl-seconds"));
        if (v == null) return 120;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 10, 1800) : 120;
    }

    public int GetPreflightIndexerMaxWaitSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.indexer-max-wait-seconds"));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 120) : 5;
    }


    public bool IsWatchtowerEnabled()
    {
        return GetBoolean("watchtower.enabled", false);
    }

    public bool IsWatchtowerAutoThroughput()
    {
        return GetBoolean("watchtower.auto-throughput", false);
    }

    public bool IsWatchtowerVerboseLoggingEnabled()
    {
        return GetBoolean("watchtower.verbose-logging", false);
    }

    public string GetWatchtowerProfileToken()
    {
        return StringUtil.EmptyToNull(GetConfigValue("watchtower.profile-token")) ?? "";
    }

    public string GetWatchtowerRanking()
    {
        return GetChoice("watchtower.ranking", "watchdog", "largest", "watchdog");
    }

    public long GetWatchtowerSizeFloorBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.size-floor-bytes"));
        if (v == null) return 536870912L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 536870912L;
    }

    public long GetWatchtowerSizeCeilingBytes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.size-ceiling-bytes"));
        if (v == null) return 0L;
        return long.TryParse(v, out var n) ? Math.Max(0, n) : 0L;
    }

    public int GetWatchtowerMinGrabs()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.min-grabs"));
        if (v == null) return 0;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 0;
    }

    public int GetWatchtowerShortlistDepth()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.shortlist-depth"));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 5) : 2;
    }

    public int GetWatchtowerGrabCapPerResolve()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.grab-cap-per-resolve"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 3;
    }

    public int GetWatchtowerVerifySampleCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.verify-sample-count"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 20) : 3;
    }

    public int GetWatchtowerVerifyTimeoutSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.verify-timeout-seconds"));
        if (v == null) return 10;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 2, 120) : 10;
    }

    public int GetWatchtowerActiveSetCap()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.active-set-cap"));
        if (v == null) return 100;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100000) : 100;
    }

    public int GetWatchtowerResolveConcurrency()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.resolve-concurrency"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 16) : 3;
    }

    public int GetWatchtowerDailyResolveBudget()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.daily-resolve-budget"));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 60;
    }

    public int GetWatchtowerSyncIntervalSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.sync-interval-seconds"));
        if (v == null) return 3600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 60, 86400) : 3600;
    }

    public int GetWatchtowerKeepFreshBaseSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.keepfresh-base-seconds"));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 300, 604800) : 21600;
    }

    public int GetWatchtowerKeepFreshMaxSeconds()
    {
        var configured = GetInteger("watchtower.keepfresh-max-seconds", 604800, 600, 2592000);
        return Math.Max(configured, GetWatchtowerKeepFreshBaseSeconds());
    }

    public int GetWatchtowerUnavailableRetrySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.unavailable-retry-seconds"));
        if (v == null) return 21600;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 600, 604800) : 21600;
    }

    public string GetWatchtowerSeriesScope()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.series-scope"));
        return NormalizeSeriesScope(v) ?? "latest-season";
    }

    public static string? NormalizeSeriesScope(string? value)
    {
        return StringUtil.EmptyToNull(value)?.Trim().ToLowerInvariant() switch
        {
            "latest-season" => "latest-season",
            "first-season" => "first-season",
            "all-aired" => "all-aired",
            "recent" => "recent",
            "off" => "off",
            _ => null,
        };
    }

    public int GetWatchtowerSeriesMaxEpisodes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.series-max-episodes"));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 1000) : 50;
    }

    public string GetWatchtowerSeriesCapKeep()
    {
        return GetChoice("watchtower.series-cap-keep", "newest", "oldest", "newest");
    }

    public int GetWatchtowerSeriesRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.series-recent-count"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 3;
    }

    public bool IsWatchtowerSeasonBundlesEnabled()
    {
        return GetBoolean("watchtower.season-bundles", true);
    }

    public bool IsWatchtowerSeasonBundleFallbackEnabled()
    {
        return GetBoolean("watchtower.season-bundle-fallback", false);
    }

    public string GetWatchtowerSeasonBundleFallbackScope()
    {
        return GetChoice("watchtower.season-bundle-fallback-scope", "latest-season",
            "all", "recent", "latest-season");
    }

    public int GetWatchtowerSeasonBundleFallbackRecentCount()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.season-bundle-fallback-recent-count"));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 100) : 2;
    }

    public int GetWatchtowerSeasonBundleFallbackMaxEpisodes()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("watchtower.season-bundle-fallback-max-episodes"));
        if (v == null) return 50;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 1000) : 50;
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        return GetBoolean("webdav.preview-par2-files", false);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        return GetBoolean("api.ignore-history-limit", true);
    }

    public bool IsRepairJobEnabled()
    {
        return GetBoolean("repair.enable", false)
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        var config = GetConfigValue<ArrConfig>("arr.instances") ?? new ArrConfig();
        config.RadarrInstances ??= [];
        config.SonarrInstances ??= [];
        config.QueueRules ??= [];
        return config;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var config = GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? new UsenetProviderConfig();
        config.Providers ??= [];
        return config;
    }

    public IndexerConfig GetIndexerConfig()
    {
        var config = GetConfigValue<IndexerConfig>("indexers.instances") ?? new IndexerConfig();
        config.Indexers ??= [];
        return config;
    }

    public ProfileConfig GetProfileConfig()
    {
        var config = GetConfigValue<ProfileConfig>("profiles.instances") ?? new ProfileConfig();
        config.Profiles ??= [];
        return config.Normalized();
    }

    public string GetDuplicateNzbBehavior()
    {
        return GetChoice("api.duplicate-nzb-behavior", "increment", "increment", "mark-failed");
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetChoice("api.import-strategy", "symlinks", "symlinks", "strm");
    }

    public string GetStrmCompletedDownloadDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.completed-downloads-dir"))?.Trim()
               ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return StringUtil.EmptyToNull(GetConfigValue("general.base-url"))?.Trim()
               ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        return GetBoolean("rclone.rc-enabled", false);
    }

    public string? GetRcloneHost()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.host"))?.Trim().TrimEnd('/');
    }

    public string? GetRcloneUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.user"))?.Trim();
    }

    public string? GetRclonePass()
    {
        return StringUtil.EmptyToNull(GetConfigValue("rclone.pass"));
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public string GetSearchUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.search-user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_SEARCH_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        return GetBoolean("db.is-startup-vacuum-enabled", false);
    }

    public bool IsNzbBackupEnabled()
    {
        return GetBoolean("api.nzb-backup-enabled", false);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-location"))?.Trim();
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        return GetBoolean("maintenance.remove-orphaned-schedule-enabled", false);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-time"));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}
