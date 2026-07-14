using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetSettingsMetadata;

[ApiController]
[Route("api/get-settings-metadata")]
// Internal bootstrap contract for the bundled frontend, not a versioned public settings schema.
public class GetSettingsMetadataController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    SettingsCoordinator coordinator) : BaseApiController
{
    private static readonly HashSet<string> MeaningfulBlankValues =
    [
        "general.base-url", "api.key", "api.ensure-article-existence-categories",
        "api.download-file-blocklist", "api.nzb-backup-location", "usenet.providers",
        "usenet.max-queue-connections", "rclone.host", "rclone.user", "rclone.pass",
        "media.library-dir", "search.exclude-patterns", "watchtower.profile-token",
    ];

    protected override async Task<IActionResult> HandleRequest()
    {
        var knownKeys = SettingsRegistry.Defaults.Keys.ToArray();
        var stored = await dbClient.Ctx.ConfigItems.AsNoTracking()
            .Where(x => knownKeys.Contains(x.ConfigName))
            .ToDictionaryAsync(x => x.ConfigName, x => x.ConfigValue, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        var items = SettingsSchema.Ordered.Select(definition =>
        {
            var key = definition.Key;
            stored.TryGetValue(key, out var storedValue);
            var effective = ResolveEffectiveValue(key, storedValue, configManager);
            var explicitValue = storedValue is not null
                && (!string.IsNullOrWhiteSpace(storedValue) || MeaningfulBlankValues.Contains(key));
            var environmentPresent = definition.EnvironmentFallback is not null
                && EnvironmentUtil.GetEnvironmentVariable(definition.EnvironmentFallback) is not null;
            var environmentActive = !explicitValue && environmentPresent;
            return new SettingMetadataItem
            {
                Key = key,
                EffectiveValue = effective,
                Source = explicitValue ? "yaml/sqlite" : environmentActive ? "environment" : "default",
                EnvironmentFallback = definition.EnvironmentFallback,
                EnvironmentPresent = environmentPresent,
                YamlPath = string.Join('.', definition.YamlPath),
                Section = definition.Section,
                Subsection = definition.Subsection,
                Order = definition.Order,
                DataType = definition.DataType.ToString().ToLowerInvariant(),
                ApplyPolicy = definition.ApplyPolicy switch
                {
                    SettingApplyPolicy.ComponentReload => "component_reload",
                    SettingApplyPolicy.RestartRequired => "restart_required",
                    _ => "immediate",
                },
                Sensitive = definition.Sensitive,
            };
        }).ToList();

        return Ok(new { status = true, revision = coordinator.Status.Revision, sync = coordinator.Status, settings = items });
    }

    internal static string ResolveEffectiveValue(string key, string? storedValue, ConfigManager config)
    {
        if (key is "webdav.pass" or "rclone.pass") return "";
        if (storedValue is not null
            && (!string.IsNullOrWhiteSpace(storedValue) || MeaningfulBlankValues.Contains(key)))
            return storedValue;

        return key switch
        {
            "api.categories" => EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                                ?? "audio,software,tv,movies",
            "api.manual-category" => config.GetManualUploadCategory(),
            "api.completed-downloads-dir" => config.GetStrmCompletedDownloadDir(),
            "api.user-agent" => config.GetUserAgent(),
            "api.search-user-agent" => config.GetSearchUserAgent(),
            "usenet.max-download-connections" => config.GetMaxDownloadConnections().ToString(),
            "usenet.segment-cache.path" => config.GetSegmentCachePath(),
            "webdav.user" => config.GetWebdavUser() ?? "",
            "rclone.mount-dir" => config.GetRcloneMountDir(),
            _ => SettingsRegistry.Defaults[key],
        };
    }

    private sealed class SettingMetadataItem
    {
        public required string Key { get; init; }
        public required string EffectiveValue { get; init; }
        public required string Source { get; init; }
        public string? EnvironmentFallback { get; init; }
        public bool EnvironmentPresent { get; init; }
        public required string YamlPath { get; init; }
        public required string Section { get; init; }
        public required string Subsection { get; init; }
        public int Order { get; init; }
        public required string DataType { get; init; }
        public required string ApplyPolicy { get; init; }
        public bool Sensitive { get; init; }
    }
}
