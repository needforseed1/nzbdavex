using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260710000000_Add-Queue-Phase-Timings")]
public partial class AddQueuePhaseTimings : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PrepDurationMs",
            table: "HistoryItems",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "HealthDurationMs",
            table: "HistoryItems",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PrepDurationMs",
            table: "WatchdogEntries",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "HealthDurationMs",
            table: "WatchdogEntries",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PrepDurationMs", table: "HistoryItems");
        migrationBuilder.DropColumn(name: "HealthDurationMs", table: "HistoryItems");
        migrationBuilder.DropColumn(name: "PrepDurationMs", table: "WatchdogEntries");
        migrationBuilder.DropColumn(name: "HealthDurationMs", table: "WatchdogEntries");
    }
}
