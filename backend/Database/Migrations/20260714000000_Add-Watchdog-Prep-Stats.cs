using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260714000000_Add-Watchdog-Prep-Stats")]
public partial class AddWatchdogPrepStats : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PrepStatsJson",
            table: "WatchdogEntries",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PrepStatsJson", table: "WatchdogEntries");
    }
}
