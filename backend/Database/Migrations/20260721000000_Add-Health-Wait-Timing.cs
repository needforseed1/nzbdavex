using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260721000000_Add-Health-Wait-Timing")]
public partial class AddHealthWaitTiming : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "HealthWaitDurationMs",
            table: "HistoryItems",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "HealthWaitDurationMs",
            table: "WatchdogEntries",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "HealthWaitDurationMs", table: "HistoryItems");
        migrationBuilder.DropColumn(name: "HealthWaitDurationMs", table: "WatchdogEntries");
    }
}
