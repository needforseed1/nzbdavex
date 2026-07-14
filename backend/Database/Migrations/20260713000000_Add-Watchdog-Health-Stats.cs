using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260713000000_Add-Watchdog-Health-Stats")]
public partial class AddWatchdogHealthStats : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "HealthStatsJson",
            table: "WatchdogEntries",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "HealthStatsJson", table: "WatchdogEntries");
    }
}
