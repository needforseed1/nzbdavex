using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.UpdateConfig;

[ApiController]
[Route("api/update-config")]
public class UpdateConfigController(DavDatabaseClient dbClient, ConfigManager configManager) : BaseApiController
{
    private async Task<UpdateConfigResponse> UpdateConfig(UpdateConfigRequest request)
    {
        ValidateConfigItems(request.ConfigItems);
        // 1. Retrieve all ConfigItems from the database that match the ConfigNames in the request
        var configNames = request.ConfigItems.Select(x => x.ConfigName).ToHashSet();
        var existingItems = await dbClient.Ctx.ConfigItems
            .Where(c => configNames.Contains(c.ConfigName))
            .ToListAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // 2. Split the items into those that need to be updated and those that need to be inserted
        var existingItemsDict = existingItems.ToDictionary(i => i.ConfigName);
        var itemsToUpdate = new List<ConfigItem>();
        var itemsToInsert = new List<ConfigItem>();
        foreach (var item in request.ConfigItems)
        {
            if (existingItemsDict.TryGetValue(item.ConfigName, out ConfigItem? existingItem))
            {
                existingItem.ConfigValue = item.ConfigValue;
                itemsToUpdate.Add(existingItem);
            }
            else
            {
                itemsToInsert.Add(item);
            }
        }

        // 3. Perform bulk insert and bulk update
        dbClient.Ctx.ConfigItems.AddRange(itemsToInsert);
        dbClient.Ctx.ConfigItems.UpdateRange(itemsToUpdate);

        // 4. Save changes in one call
        await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);

        // 5. Update the ConfigManager
        configManager.UpdateValues(request.ConfigItems);

        // return
        return new UpdateConfigResponse { Status = true };
    }

    internal static void ValidateConfigItems(IReadOnlyList<ConfigItem> items)
    {
        var changed = items
            .GroupBy(x => x.ConfigName, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Last().ConfigValue, StringComparer.Ordinal);
        foreach (var (key, value) in changed)
            ValidateRegisteredSetting(key, value);
        if (changed.TryGetValue("search.exclude-patterns", out var raw))
            ValidateSearchPatterns(raw);
        if (changed.TryGetValue("usenet.providers", out raw))
            ValidateProviders(raw);
        if (changed.TryGetValue("indexers.instances", out raw))
            ValidateIndexers(raw);
        if (changed.TryGetValue("profiles.instances", out raw))
            ValidateProfiles(raw);
        if (changed.TryGetValue("arr.instances", out raw))
            ValidateArr(raw);
        foreach (var key in new[] { "api.user-agent", "api.search-user-agent" })
            if (changed.TryGetValue(key, out raw) && ContainsNewline(raw))
                Invalid($"{key} cannot contain line breaks.");
    }

    private static void ValidateRegisteredSetting(string key, string value)
    {
        if (!SettingsRegistry.Defaults.ContainsKey(key)) return;
        var descriptor = SettingsRegistry.Describe(key);
        if (descriptor.Type == "boolean" && !bool.TryParse(value, out _))
            Invalid($"{key} must be true or false.");
        if (descriptor.Type == "integer")
        {
            // A few numeric settings deliberately use blank as "automatic".
            if (value.Length == 0 && key == "usenet.max-queue-connections") return;
            if (!long.TryParse(value, out var number)) Invalid($"{key} must be an integer.");
            if (descriptor.Min is { } min && number < min || descriptor.Max is { } max && number > max)
                Invalid($"{key} must be between {descriptor.Min} and {descriptor.Max}.");
        }
        if (descriptor.Choices is { Count: > 0 }
            && !descriptor.Choices.Contains(value, StringComparer.OrdinalIgnoreCase))
            Invalid($"{key} must be one of: {string.Join(", ", descriptor.Choices)}.");
    }

    private static void ValidateSearchPatterns(string raw)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var pattern = lines[i].Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#')) continue;
            try
            {
                _ = new Regex(pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException e)
            {
                throw new BadHttpRequestException(
                    $"Invalid search exclusion regex on line {i + 1}: {e.Message}");
            }
        }
    }

    private static void ValidateProviders(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var config = Deserialize<UsenetProviderConfig>(raw, "Usenet providers");
        if (config.Providers is null) Invalid("Usenet providers must be an array.");

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in config.Providers)
        {
            if (p is null) Invalid("Usenet providers cannot contain null entries.");
            if (!Guid.TryParse(p.Id, out _) || !providerIds.Add(p.Id))
                Invalid("Every Usenet provider needs a unique stable ID.");
            if (!Enum.IsDefined(p.Type)) Invalid("A Usenet provider has an invalid role.");
            if (string.IsNullOrWhiteSpace(p.Host)) Invalid("Every Usenet provider needs a host.");
            if (p.Port is < 1 or > 65535) Invalid("Usenet provider ports must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(p.User) || string.IsNullOrWhiteSpace(p.Pass))
                Invalid("Every Usenet provider needs a username and password.");
            if (p.MaxConnections is < 1 or > 1024)
                Invalid("Usenet provider connections must be between 1 and 1024.");
            if (p.PipeliningDepth is not null and (< 1 or > 64)
                || p.HealthPipeliningDepth is not null and (< 1 or > 64))
                Invalid("Usenet provider pipeline depths must be between 1 and 64.");
            if (p.ByteLimit is < 0) Invalid("A Usenet provider data cap cannot be negative.");
        }
    }

    private static void ValidateIndexers(string raw)
    {
        var config = Deserialize<IndexerConfig>(raw, "Indexer settings");
        if (config.Indexers is null) Invalid("Indexers must be an array.");
        if (!OptionalHttpUrl(config.ProxyUrl)) Invalid("The global indexer proxy must be an HTTP(S) URL.");
        if (config.TimeoutSeconds is <= 0 or > 3600)
            Invalid("The global indexer timeout must be between 1 and 3600 seconds.");
        if (config.SearchResultLimit is <= 0 or > 5000)
            Invalid("The global indexer result limit must be between 1 and 5000.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in config.Indexers)
        {
            if (x is null) Invalid("Indexers cannot contain null entries.");
            if (!Guid.TryParse(x.Id, out _) || !ids.Add(x.Id))
                Invalid("Every indexer needs a unique stable ID.");
            if (string.IsNullOrWhiteSpace(x.Name) || !names.Add(x.Name.Trim()))
                Invalid("Indexer names must be non-empty and unique.");
            if (!HttpUrl(x.Url) || string.IsNullOrWhiteSpace(x.ApiKey))
                Invalid($"Indexer {x.Name} needs an HTTP(S) URL and API key.");
            if (!OptionalHttpUrl(x.ProxyUrl)) Invalid($"Indexer {x.Name} has an invalid proxy URL.");
            if (ContainsNewline(x.SearchUserAgent) || ContainsNewline(x.RetrieveUserAgent))
                Invalid($"Indexer {x.Name} user-agent values cannot contain line breaks.");
            if (x.MaxRequestsPerMinute is < 0 or > 10000)
                Invalid($"Indexer {x.Name} requests per minute must be between 0 and 10000.");
            if (x.TimeoutSeconds is <= 0 or > 3600)
                Invalid($"Indexer {x.Name} timeout must be between 1 and 3600 seconds.");
            if (x.SearchResultLimit is <= 0 or > 5000)
                Invalid($"Indexer {x.Name} result limit must be between 1 and 5000.");
            if (x.HitLimit is < 0 || x.DownloadLimit is < 0)
                Invalid($"Indexer {x.Name} hit limits cannot be negative.");
            if (x.HitLimitResetTime is < 0 or > 23)
                Invalid($"Indexer {x.Name} reset hour must be between 0 and 23.");
            if (!CategoryList(x.ExtraMovieCategories) || !CategoryList(x.ExtraTvCategories))
                Invalid($"Indexer {x.Name} categories must be comma-separated numbers.");
            if (x.Filter is { } filter
                && (filter.MinGrabs < 0 || filter.GrabsGraceHours < 0 || filter.MaxAgeDaysWithoutGrabs < 0))
                Invalid($"Indexer {x.Name} result-filter values cannot be negative.");
        }
    }

    private static void ValidateProfiles(string raw)
    {
        var config = Deserialize<ProfileConfig>(raw, "Search Profiles");
        if (config.Profiles is null) Invalid("Search Profiles must be an array.");
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var allowedAdapters = new HashSet<string>(["json", "newznab", "addon"], StringComparer.OrdinalIgnoreCase);
        foreach (var p in config.Profiles)
        {
            if (p is null) Invalid("Search Profiles cannot contain null entries.");
            if (string.IsNullOrWhiteSpace(p.Token) || !tokens.Add(p.Token))
                Invalid("Search Profile tokens must be non-empty and unique.");
            if (string.IsNullOrWhiteSpace(p.Name)) Invalid("Every Search Profile needs a name.");
            if (p.IndexerIds is null || p.IndexerIds.Any(x => !Guid.TryParse(x, out _)))
                Invalid($"Search Profile {p.Name} has an invalid indexer ID list.");
            if (p.EnabledAdapters?.Any(x => !allowedAdapters.Contains(x)) == true)
                Invalid($"Search Profile {p.Name} contains an unknown adapter.");
            if (!Enum.IsDefined(p.MovieFallback) || !Enum.IsDefined(p.TvFallback)
                || p.MovieFallback == ProfileConfig.FallbackMode.Broad)
                Invalid($"Search Profile {p.Name} has an invalid fallback mode.");
            if (p.MovieFallbackMinResults is < 1 or > 5000
                || p.TvFallbackMinResults is < 1 or > 5000
                || p.QueryFallbackMinResults is < 1 or > 5000)
                Invalid($"Search Profile {p.Name} fallback thresholds must be between 1 and 5000.");
        }
    }

    private static void ValidateArr(string raw)
    {
        var config = Deserialize<ArrConfig>(raw, "Radarr/Sonarr settings");
        if (config.RadarrInstances is null || config.SonarrInstances is null || config.QueueRules is null)
            Invalid("Radarr/Sonarr instance and queue-rule values must be arrays.");
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in config.RadarrInstances.Concat(config.SonarrInstances))
        {
            if (instance is null) Invalid("Radarr/Sonarr instances cannot contain null entries.");
            if (!HttpUrl(instance.Host) || string.IsNullOrWhiteSpace(instance.ApiKey))
                Invalid("Every Radarr/Sonarr instance needs an HTTP(S) URL and API key.");
            if (!hosts.Add(instance.Host.Trim().TrimEnd('/')))
                Invalid("Duplicate Radarr/Sonarr instance URLs are not allowed.");
        }
        if (config.QueueRules.Any(x => x is null
            || string.IsNullOrWhiteSpace(x.Message) || !Enum.IsDefined(x.Action)))
            Invalid("Radarr/Sonarr queue rules contain an invalid message or action.");
    }

    private static T Deserialize<T>(string raw, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? throw new JsonException("value is null");
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new BadHttpRequestException($"{label} contain invalid JSON: {e.Message}");
        }
    }

    private static bool HttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    private static bool OptionalHttpUrl(string? value) => string.IsNullOrWhiteSpace(value) || HttpUrl(value);

    private static bool CategoryList(string? value) => string.IsNullOrWhiteSpace(value)
        || value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(x => x.All(char.IsDigit));

    private static bool ContainsNewline(string? value) => value?.IndexOfAny(['\r', '\n']) >= 0;

    [DoesNotReturn]
    private static void Invalid(string message) => throw new BadHttpRequestException(message);

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new UpdateConfigRequest(HttpContext);
        var response = await UpdateConfig(request).ConfigureAwait(false);
        return Ok(response);
    }
}
