using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260712010000_Add-Stable-Config-Ids")]
public class AddStableConfigIds : Migration
{
    private const string NewGuidSql = "lower(hex(randomblob(4))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(2))) || '-' || lower(hex(randomblob(6)))";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "LastAccessedAtUnix",
            table: "WantedItems",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_WantedItems_State_LastAccessedAtUnix",
            table: "WantedItems",
            columns: ["State", "LastAccessedAtUnix"]);

        migrationBuilder.Sql($$"""
            UPDATE ConfigItems
            SET ConfigValue = json_set(
                ConfigValue,
                '$.Providers',
                json(COALESCE((
                    SELECT json_group_array(json(
                        CASE WHEN coalesce(json_extract(p.value, '$.Id'), '') = ''
                            THEN json_set(p.value, '$.Id', {{NewGuidSql}})
                            ELSE p.value END
                    ))
                    FROM json_each(ConfigValue, '$.Providers') p
                ), '[]')))
            WHERE ConfigName = 'usenet.providers' AND json_valid(ConfigValue);

            UPDATE ConfigItems
            SET ConfigValue = json_set(
                ConfigValue,
                '$.Indexers',
                json(COALESCE((
                    SELECT json_group_array(json(
                        CASE WHEN coalesce(json_extract(i.value, '$.Id'), '') = ''
                            THEN json_set(i.value, '$.Id', {{NewGuidSql}})
                            ELSE i.value END
                    ))
                    FROM json_each(ConfigValue, '$.Indexers') i
                ), '[]')))
            WHERE ConfigName = 'indexers.instances' AND json_valid(ConfigValue);

            UPDATE IndexerApiHits
            SET IndexerName = COALESCE((
                SELECT json_extract(i.value, '$.Id')
                FROM ConfigItems c, json_each(c.ConfigValue, '$.Indexers') i
                WHERE c.ConfigName = 'indexers.instances'
                  AND lower(json_extract(i.value, '$.Name')) = lower(IndexerApiHits.IndexerName)
                LIMIT 1
            ), IndexerName);

            UPDATE ConfigItems AS profiles
            SET ConfigValue = json_set(
                profiles.ConfigValue,
                '$.Profiles',
                json(COALESCE((
                    SELECT json_group_array(json(
                        json_remove(
                            json_set(profile.value, '$.IndexerIds', json(COALESCE((
                                SELECT json_group_array(json_extract(idx.value, '$.Id'))
                                FROM ConfigItems indexers,
                                     json_each(indexers.ConfigValue, '$.Indexers') idx
                                WHERE indexers.ConfigName = 'indexers.instances'
                                  AND lower(json_extract(idx.value, '$.Name')) IN (
                                      SELECT lower(name.value)
                                      FROM json_each(profile.value, '$.IndexerNames') name
                                  )
                            ), '[]'))),
                            '$.IndexerNames'
                        )
                    ))
                    FROM json_each(profiles.ConfigValue, '$.Profiles') profile
                ), '[]')))
            WHERE profiles.ConfigName = 'profiles.instances' AND json_valid(profiles.ConfigValue);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE IndexerApiHits
            SET IndexerName = COALESCE((
                SELECT json_extract(i.value, '$.Name')
                FROM ConfigItems c, json_each(c.ConfigValue, '$.Indexers') i
                WHERE c.ConfigName = 'indexers.instances'
                  AND json_extract(i.value, '$.Id') = IndexerApiHits.IndexerName
                LIMIT 1
            ), IndexerName);

            UPDATE ConfigItems AS profiles
            SET ConfigValue = json_set(
                profiles.ConfigValue,
                '$.Profiles',
                json(COALESCE((
                    SELECT json_group_array(json(
                        json_remove(
                            json_set(profile.value, '$.IndexerNames', json(COALESCE((
                                SELECT json_group_array(json_extract(idx.value, '$.Name'))
                                FROM ConfigItems indexers,
                                     json_each(indexers.ConfigValue, '$.Indexers') idx
                                WHERE indexers.ConfigName = 'indexers.instances'
                                  AND json_extract(idx.value, '$.Id') IN (
                                      SELECT id.value FROM json_each(profile.value, '$.IndexerIds') id
                                  )
                            ), '[]'))),
                            '$.IndexerIds'
                        )
                    )) FROM json_each(profiles.ConfigValue, '$.Profiles') profile
                ), '[]')))
            WHERE profiles.ConfigName = 'profiles.instances' AND json_valid(profiles.ConfigValue);

            UPDATE ConfigItems
            SET ConfigValue = json_set(ConfigValue, '$.Providers', json(COALESCE((
                SELECT json_group_array(json(json_remove(p.value, '$.Id')))
                FROM json_each(ConfigValue, '$.Providers') p
            ), '[]')))
            WHERE ConfigName = 'usenet.providers' AND json_valid(ConfigValue);

            UPDATE ConfigItems
            SET ConfigValue = json_set(ConfigValue, '$.Indexers', json(COALESCE((
                SELECT json_group_array(json(json_remove(i.value, '$.Id')))
                FROM json_each(ConfigValue, '$.Indexers') i
            ), '[]')))
            WHERE ConfigName = 'indexers.instances' AND json_valid(ConfigValue);
            """);
        migrationBuilder.DropIndex(
            name: "IX_WantedItems_State_LastAccessedAtUnix",
            table: "WantedItems");
        migrationBuilder.DropColumn(
            name: "LastAccessedAtUnix",
            table: "WantedItems");
    }
}
