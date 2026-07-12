using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260712000000_Migrate-Legacy-Settings")]
public partial class MigrateLegacySettings : Migration
{
    internal const string MigrateExcludePatternsSql = """
        INSERT INTO ConfigItems (ConfigName, ConfigValue)
        SELECT 'search.exclude-patterns', ConfigValue
        FROM ConfigItems
        WHERE ConfigName = 'play.exclude-patterns'
          AND NOT EXISTS (
              SELECT 1 FROM ConfigItems WHERE ConfigName = 'search.exclude-patterns'
          );

        DELETE FROM ConfigItems WHERE ConfigName = 'play.exclude-patterns';
        """;

    internal const string MigrateIndexerUserAgentsSql = """
        UPDATE ConfigItems
        SET ConfigValue = json_set(
            ConfigValue,
            '$.Indexers',
            json((
                SELECT json_group_array(json(
                    CASE
                        WHEN json_type(entry.value, '$.UserAgent') IS NOT NULL THEN
                            json_remove(
                                json_set(
                                    entry.value,
                                    '$.SearchUserAgent',
                                    CASE
                                        WHEN NULLIF(TRIM(CAST(json_extract(entry.value, '$.SearchUserAgent') AS TEXT)), '') IS NULL
                                            THEN json_extract(entry.value, '$.UserAgent')
                                        ELSE json_extract(entry.value, '$.SearchUserAgent')
                                    END,
                                    '$.RetrieveUserAgent',
                                    CASE
                                        WHEN NULLIF(TRIM(CAST(json_extract(entry.value, '$.RetrieveUserAgent') AS TEXT)), '') IS NULL
                                            THEN json_extract(entry.value, '$.UserAgent')
                                        ELSE json_extract(entry.value, '$.RetrieveUserAgent')
                                    END
                                ),
                                '$.UserAgent'
                            )
                        ELSE entry.value
                    END
                ))
                FROM json_each(ConfigValue, '$.Indexers') AS entry
            ))
        )
        WHERE ConfigName = 'indexers.instances'
          AND json_valid(ConfigValue)
          AND json_type(ConfigValue, '$.Indexers') = 'array'
          AND EXISTS (
              SELECT 1
              FROM json_each(ConfigValue, '$.Indexers') AS entry
              WHERE json_type(entry.value, '$.UserAgent') IS NOT NULL
          );
        """;

    internal const string RestoreLegacyIndexerUserAgentsSql = """
        UPDATE ConfigItems
        SET ConfigValue = json_set(
            ConfigValue,
            '$.Indexers',
            json((
                SELECT json_group_array(json(
                    CASE
                        WHEN json_type(entry.value, '$.UserAgent') IS NULL
                             AND (json_type(entry.value, '$.SearchUserAgent') IS NOT NULL
                                  OR json_type(entry.value, '$.RetrieveUserAgent') IS NOT NULL) THEN
                            json_set(
                                entry.value,
                                '$.UserAgent',
                                COALESCE(
                                    NULLIF(TRIM(CAST(json_extract(entry.value, '$.RetrieveUserAgent') AS TEXT)), ''),
                                    json_extract(entry.value, '$.SearchUserAgent')
                                )
                            )
                        ELSE entry.value
                    END
                ))
                FROM json_each(ConfigValue, '$.Indexers') AS entry
            ))
        )
        WHERE ConfigName = 'indexers.instances'
          AND json_valid(ConfigValue)
          AND json_type(ConfigValue, '$.Indexers') = 'array';
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(MigrateExcludePatternsSql);
        migrationBuilder.Sql(MigrateIndexerUserAgentsSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO ConfigItems (ConfigName, ConfigValue)
            SELECT 'play.exclude-patterns', ConfigValue
            FROM ConfigItems
            WHERE ConfigName = 'search.exclude-patterns'
              AND NOT EXISTS (
                  SELECT 1 FROM ConfigItems WHERE ConfigName = 'play.exclude-patterns'
              );
            """);
        migrationBuilder.Sql(RestoreLegacyIndexerUserAgentsSql);
    }
}
