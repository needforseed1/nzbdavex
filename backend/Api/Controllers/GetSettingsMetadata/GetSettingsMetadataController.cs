using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetSettingsMetadata;

[ApiController]
[Route("api/get-settings-metadata")]
// Internal bootstrap contract for the bundled frontend, not a versioned public settings schema.
public class GetSettingsMetadataController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
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

        var items = SettingsRegistry.Defaults.Keys.Select(key =>
        {
            stored.TryGetValue(key, out var storedValue);
            var effective = ResolveEffectiveValue(key, storedValue, configManager);
            return new SettingMetadataItem { Key = key, EffectiveValue = effective };
        }).ToList();

        return Ok(new { status = true, settings = items });
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
    }
}
