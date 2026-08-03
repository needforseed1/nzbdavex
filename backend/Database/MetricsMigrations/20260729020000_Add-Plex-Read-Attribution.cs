using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

[DbContext(typeof(MetricsDbContext))]
[Migration("20260729020000_Add-Plex-Read-Attribution")]
public partial class AddPlexReadAttribution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PlexPurpose",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 64,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PlexConfidence",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PlexProduct",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 255,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PlexPlayer",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 255,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PlexPlatform",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 255,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PlexRatingKey",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 255,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "PlexDetail",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 512,
            nullable: true);
        migrationBuilder.AddColumn<bool>(
            name: "PlexIsTranscode",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PlexPurpose", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexConfidence", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexProduct", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexPlayer", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexPlatform", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexRatingKey", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexDetail", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexIsTranscode", table: "ReadSessions");
    }
}
