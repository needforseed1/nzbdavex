using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

/// <summary>
/// Splits upstream waits by cause. A stall count alone cannot tell "the source
/// could not deliver in time" apart from "segments were already downloaded and
/// stuck behind one slow article", yet the two call for opposite fixes: fetch
/// harder, or stop one article blocking the queue. Existing rows default to 0,
/// which reads as "cause not recorded" rather than "never head-of-line".
/// </summary>
[DbContext(typeof(MetricsDbContext))]
[Migration("20260728010000_Add-Head-Of-Line-Stalls")]
public partial class AddHeadOfLineStalls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "HeadOfLineStalls",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<long>(
            name: "TotalHeadOfLineStallMs",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "HeadOfLineStalls", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "TotalHeadOfLineStallMs", table: "ReadSessions");
    }
}
