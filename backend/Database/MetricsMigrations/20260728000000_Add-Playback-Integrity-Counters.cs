using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

/// <summary>
/// Zero-filled articles and recovered body stalls. Every existing counter on a
/// session describes delay; these two describe the stream being wrong or the
/// connection wedging, which until now left no trace on the row at all — a play
/// that served substituted zeros was reported as clean. Existing rows default
/// to 0: their integrity is simply unknown, not proven good.
/// </summary>
[DbContext(typeof(MetricsDbContext))]
[Migration("20260728000000_Add-Playback-Integrity-Counters")]
public partial class AddPlaybackIntegrityCounters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ZeroFilledSegments",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<long>(
            name: "ZeroFilledBytes",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
        migrationBuilder.AddColumn<int>(
            name: "BodyStallRecoveries",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ZeroFilledSegments", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "ZeroFilledBytes", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "BodyStallRecoveries", table: "ReadSessions");
    }
}
