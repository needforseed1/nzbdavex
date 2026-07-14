using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using System.Text.Json;

namespace NzbWebDAV.Tests.Config;

public class SettingsSchemaTests
{
    [Fact]
    public void SchemaContainsEveryGuiSettingExactlyOnce()
    {
        Assert.Equal(SettingsRegistry.Defaults.Count, SettingsSchema.Ordered.Count);
        Assert.Equal(SettingsSchema.Ordered.Count, SettingsSchema.Ordered.Select(x => x.Key).Distinct().Count());
        Assert.Equal(SettingsRegistry.Defaults.Keys.Order(), SettingsSchema.Ordered.Select(x => x.Key).Order());
        Assert.Equal(Enumerable.Range(0, SettingsSchema.Ordered.Count), SettingsSchema.Ordered.Select(x => x.Order));
    }

    [Fact]
    public void TopLevelSchemaOrderMatchesSettingsGui()
    {
        var sections = SettingsSchema.Ordered.Select(x => x.Section).Distinct().ToArray();
        Assert.Equal(
            ["usenet", "indexers", "search_profiles", "watchdog", "preflight", "watchtower", "warden", "advanced"],
            sections);
    }

    [Fact]
    public void EveryVisibleGuiSectionAppearsInCanonicalYamlOrder()
    {
        var visible = SettingsSchema.Ordered.Select(x => x.Key switch
            {
                var key when key.StartsWith("usenet.") => "usenet",
                "indexers.instances" or "search.exclude-patterns" or "api.search-user-agent" or "api.user-agent" => "indexers",
                "profiles.instances" or "general.base-url" => "profiles",
                var key when key.StartsWith("play.") || key.StartsWith("grab.") || key.StartsWith("variants.") => "watchdog",
                var key when key.StartsWith("preflight.") => "preflight",
                var key when key.StartsWith("watchtower.") => "watchtower",
                var key when key.StartsWith("warden.") => "warden",
                var key when key.StartsWith("webdav.") => "webdav",
                "arr.instances" => "arrs",
                "repair.enable" or "media.library-dir" => "repairs",
                var key when key.StartsWith("rclone.") && key != "rclone.mount-dir" => "rclone",
                var key when key.StartsWith("db.") || key.StartsWith("maintenance.") => "maintenance",
                _ => "sabnzbd",
            })
            .Distinct()
            .ToArray();

        Assert.Equal([
            "usenet", "indexers", "profiles", "watchdog", "preflight", "watchtower", "warden",
            "webdav", "sabnzbd", "arrs", "repairs", "rclone", "maintenance",
        ], visible);
    }

    [Fact]
    public void CanonicalYamlIsTypedDeterministicAndRoundTrips()
    {
        var stored = SettingsRegistry.Defaults.ToDictionary(x => x.Key, x => x.Value);
        stored["usenet.providers"] = "";
        stored["api.key"] = "explicit-api-key";
        var extras = new SettingsFileExtras(
            [new WardenRemoteSourceSetting("Community", "https://example.test/warden.gz", true, "corroborate", 24)],
            new WardenBackupFileSetting(true, "owner/repo", "warden/data.gz", "main", "local", 24, "token"));
        var codec = new SettingsYamlCodec();

        var yaml = codec.Serialize(stored, extras, 7);
        var second = codec.Serialize(stored, extras, 7);
        var imported = codec.Deserialize(yaml);

        Assert.Equal(yaml, second);
        Assert.Contains("version: 1", yaml);
        Assert.Contains("enabled: false", yaml);
        Assert.Contains("key: explicit-api-key", yaml);
        Assert.Equal(7, imported.FileRevision);
        Assert.Equal("explicit-api-key", imported.Values["api.key"]);
        Assert.Single(imported.Extras.RemoteSources);
        Assert.Equal("token", imported.Extras.Backup.Token);
        Assert.Contains("\"Providers\":[]", imported.Values["usenet.providers"]);
    }

    [Fact]
    public void EnvironmentBackedUnsetValuesStayInherited()
    {
        var codec = new SettingsYamlCodec();
        var yaml = codec.Serialize(new Dictionary<string, string>(), EmptyExtras(), 0);
        var imported = codec.Deserialize(yaml);

        Assert.Null(imported.Values["api.categories"]);
        Assert.Null(imported.Values["rclone.mount-dir"]);
        Assert.Null(imported.Values["webdav.pass"]);
        Assert.DoesNotContain(EnvironmentUtil.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY") ?? "never", yaml);
    }

    [Fact]
    public void ProviderAdapterUsesReadableNamesAndUtcTimestamp()
    {
        var providers = new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Id = "11111111-1111-1111-1111-111111111111",
                    Nickname = "Primary",
                    Type = ProviderType.Pooled,
                    Host = "news.example.test",
                    Port = 563,
                    UseSsl = true,
                    User = "user",
                    Pass = "secret",
                    MaxConnections = 20,
                    BytesUsedResetAt = 1_700_000_000_000,
                },
            ],
        };
        var codec = new SettingsYamlCodec();
        var stored = new Dictionary<string, string>
        {
            ["usenet.providers"] = JsonSerializer.Serialize(providers),
        };

        var yaml = codec.Serialize(stored, EmptyExtras(), 1);
        var imported = codec.Deserialize(yaml);
        var roundTrip = JsonSerializer.Deserialize<UsenetProviderConfig>(imported.Values["usenet.providers"]!)!;

        Assert.Contains("role: pooled", yaml);
        Assert.Contains("username: user", yaml);
        Assert.Contains("password: secret", yaml);
        Assert.Contains("usage_reset_at: 2023-11-14T22:13:20.0000000Z", yaml);
        Assert.Equal(1_700_000_000_000, Assert.Single(roundTrip.Providers).BytesUsedResetAt);
    }

    [Fact]
    public void UnknownFieldsAreRejected()
    {
        var codec = new SettingsYamlCodec();
        var yaml = codec.Serialize(new Dictionary<string, string>(), EmptyExtras(), 0)
            + "unknown: true\n";

        var error = Assert.Throws<SettingsYamlException>(() => codec.Deserialize(yaml));
        Assert.Contains("Unknown", error.Message);
    }

    [Fact]
    public void UnknownCompoundFieldsAreRejected()
    {
        var codec = new SettingsYamlCodec();
        var yaml = codec.Serialize(new Dictionary<string, string>(), EmptyExtras(), 0)
            .Replace("    proxy_url: null", "    proxy_url: null\n    mystery_option: true", StringComparison.Ordinal);

        var error = Assert.Throws<SettingsYamlException>(() => codec.Deserialize(yaml));
        Assert.Contains("indexers.instances", error.Message);
    }

    [Fact]
    public void PlaintextWebdavPasswordIsImportedAsHash()
    {
        var codec = new SettingsYamlCodec();
        var yaml = codec.Serialize(new Dictionary<string, string>(), EmptyExtras(), 0);
        var hashLine = yaml.Split('\n').Single(x => x.TrimStart().StartsWith("password_hash:", StringComparison.Ordinal));
        var indentation = hashLine[..^hashLine.TrimStart().Length];
        yaml = yaml.Replace(hashLine, indentation + "password: correct-horse-battery-staple", StringComparison.Ordinal);

        var imported = codec.Deserialize(yaml);

        Assert.True(PasswordUtil.Verify(imported.Values["webdav.pass"]!, "correct-horse-battery-staple"));
        Assert.DoesNotContain("correct-horse-battery-staple", codec.Serialize(
            new Dictionary<string, string> { ["webdav.pass"] = imported.Values["webdav.pass"]! },
            EmptyExtras(), 1));
    }

    private static SettingsFileExtras EmptyExtras() => new(
        [], new WardenBackupFileSetting(false, "", "warden/warden.ndjson.gz", "main", "local", 24, null));
}
