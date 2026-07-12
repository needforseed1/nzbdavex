using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.GetSettingsMetadata;

[ApiController]
[Route("api/get-settings-metadata")]
public class GetSettingsMetadataController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var knownKeys = SettingsRegistry.Defaults.Keys.ToArray();
        var stored = await dbClient.Ctx.ConfigItems.AsNoTracking()
            .Where(x => knownKeys.Contains(x.ConfigName))
            .ToDictionaryAsync(x => x.ConfigName, x => x.ConfigValue, HttpContext.RequestAborted)
            .ConfigureAwait(false);

        var items = SettingsRegistry.Defaults.Keys.Select(key =>
        {
            var descriptor = SettingsRegistry.Describe(key);
            var source = "default";
            var effective = descriptor.DefaultValue;
            if (stored.TryGetValue(key, out var storedValue))
            {
                source = "stored";
                effective = storedValue;
            }
            else if (EnvironmentFallback(key) is { } environmentValue)
            {
                source = "environment";
                effective = environmentValue;
            }
            else if (key == "usenet.max-download-connections")
            {
                effective = configManager.GetMaxDownloadConnections().ToString();
            }

            if (key is "webdav.pass" or "rclone.pass") effective = "";
            return new SettingMetadataItem
            {
                Key = key, EffectiveValue = effective, Source = source,
                DefaultValue = descriptor.DefaultValue, Type = descriptor.Type,
                Secret = descriptor.Secret, RestartRequired = descriptor.RestartRequired,
                Min = descriptor.Min, Max = descriptor.Max, Choices = descriptor.Choices,
            };
        }).ToList();

        return Ok(new { status = true, settings = items });
    }

    private static string? EnvironmentFallback(string key)
    {
        var variable = key switch
        {
            "rclone.mount-dir" => "MOUNT_DIR",
            "api.categories" => "CATEGORIES",
            "webdav.user" => "WEBDAV_USER",
            "api.user-agent" => "NZB_GRAB_USER_AGENT",
            "api.search-user-agent" => "NZB_SEARCH_USER_AGENT",
            _ => null,
        };
        return variable is null ? null : EnvironmentUtil.GetEnvironmentVariable(variable)?.Trim();
    }

    private sealed class SettingMetadataItem
    {
        public required string Key { get; init; }
        public required string EffectiveValue { get; init; }
        public required string Source { get; init; }
        public required string DefaultValue { get; init; }
        public required string Type { get; init; }
        public bool Secret { get; init; }
        public bool RestartRequired { get; init; }
        public long? Min { get; init; }
        public long? Max { get; init; }
        public IReadOnlyList<string>? Choices { get; init; }
    }
}
