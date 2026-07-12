using System.Text.Json;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database.Migrations;

namespace NzbWebDAV.Tests.Config;

public class LegacySettingsMigrationTests
{
    [Fact]
    public void MigrationPreservesExplicitCurrentValuesAndRemovesAliases()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);");
        Insert(connection, "play.exclude-patterns", "legacy-pattern");
        Insert(connection, "search.exclude-patterns", "");
        Insert(connection, "indexers.instances", """
            {
              "ProxyUrl":"http://proxy:8080",
              "Indexers":[
                {
                  "Name":"Legacy",
                  "Url":"https://indexer.example",
                  "ApiKey":"secret",
                  "UserAgent":"legacy-agent",
                  "RetrieveUserAgent":"specific-retrieve",
                  "UnknownField":"preserve-me"
                },
                {
                  "Name":"Current",
                  "Url":"https://current.example",
                  "ApiKey":"secret",
                  "SearchUserAgent":"search-agent"
                }
              ]
            }
            """);

        Execute(connection, MigrateLegacySettings.MigrateExcludePatternsSql);
        Execute(connection, MigrateLegacySettings.MigrateIndexerUserAgentsSql);

        Assert.Equal("", Read(connection, "search.exclude-patterns"));
        Assert.Null(Read(connection, "play.exclude-patterns"));

        using var document = JsonDocument.Parse(Read(connection, "indexers.instances")!);
        var root = document.RootElement;
        Assert.Equal("http://proxy:8080", root.GetProperty("ProxyUrl").GetString());
        var legacy = root.GetProperty("Indexers")[0];
        Assert.False(legacy.TryGetProperty("UserAgent", out _));
        Assert.Equal("legacy-agent", legacy.GetProperty("SearchUserAgent").GetString());
        Assert.Equal("specific-retrieve", legacy.GetProperty("RetrieveUserAgent").GetString());
        Assert.Equal("preserve-me", legacy.GetProperty("UnknownField").GetString());

        var current = root.GetProperty("Indexers")[1];
        Assert.Equal("search-agent", current.GetProperty("SearchUserAgent").GetString());
        Assert.False(current.TryGetProperty("RetrieveUserAgent", out _));
    }

    [Fact]
    public void MigrationCopiesLegacyExcludePatternsWhenCurrentKeyIsAbsent()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection, "CREATE TABLE ConfigItems (ConfigName TEXT PRIMARY KEY, ConfigValue TEXT NOT NULL);");
        Insert(connection, "play.exclude-patterns", "legacy-pattern");

        Execute(connection, MigrateLegacySettings.MigrateExcludePatternsSql);

        Assert.Equal("legacy-pattern", Read(connection, "search.exclude-patterns"));
        Assert.Null(Read(connection, "play.exclude-patterns"));
    }

    private static void Insert(SqliteConnection connection, string name, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ConfigItems (ConfigName, ConfigValue) VALUES ($name, $value);";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static string? Read(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ConfigValue FROM ConfigItems WHERE ConfigName = $name;";
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() as string;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
