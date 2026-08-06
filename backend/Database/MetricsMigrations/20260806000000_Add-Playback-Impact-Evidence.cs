using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

[DbContext(typeof(MetricsDbContext))]
[Migration("20260806000000_Add-Playback-Impact-Evidence")]
public partial class AddPlaybackImpactEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "UpstreamWaitWallMs",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
        migrationBuilder.AddColumn<int>(
            name: "MaxUpstreamWaitWallMs",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<string>(
            name: "PlexPlaybackImpact",
            table: "ReadSessions",
            type: "TEXT",
            maxLength: 32,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "UpstreamWaitWallMs", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "MaxUpstreamWaitWallMs", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "PlexPlaybackImpact", table: "ReadSessions");
    }
}
