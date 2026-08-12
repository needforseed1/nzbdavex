using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

[DbContext(typeof(MetricsDbContext))]
[Migration("20260812010000_Add-Playback-Read-Ahead")]
public partial class AddPlaybackReadAhead : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "AverageReadAheadBytes",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "MinimumReadAheadBytes",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AverageReadAheadBytes", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "MinimumReadAheadBytes", table: "ReadSessions");
    }
}
