using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

/// <summary>
/// Stall count and worst stall cannot answer how much of a play was spent
/// waiting, which is the number that decides whether a viewer noticed. Existing
/// rows default to 0 and stay readable; their totals are simply unknown.
/// </summary>
[DbContext(typeof(MetricsDbContext))]
[Migration("20260727000000_Add-Playback-Stall-Totals")]
public partial class AddPlaybackStallTotals : Migration
{
    private static readonly string[] TotalColumns =
    [
        "TotalUpstreamStallMs",
        "TotalDownstreamStallMs",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var column in TotalColumns)
            migrationBuilder.AddColumn<long>(
                name: column,
                table: "ReadSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var column in TotalColumns)
            migrationBuilder.DropColumn(name: column, table: "ReadSessions");
    }
}
