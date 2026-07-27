using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

[DbContext(typeof(MetricsDbContext))]
[Migration("20260726000000_Add-Playback-Session-Diagnostics")]
public partial class AddPlaybackSessionDiagnostics : Migration
{
    private static readonly string[] CounterColumns =
    [
        "RequestCount",
        "UpstreamStalls",
        "MaxUpstreamStallMs",
        "DownstreamStalls",
        "MaxDownstreamStallMs",
        "FallbackRescues",
        "ProviderRotations",
        "FallbackBudgetExhaustions",
        "CacheHits",
        "CacheMisses",
        "ConnectionPermitWaits",
        "MaxConnectionPermitWaitMs",
        "ProviderPoolWaits",
        "MaxProviderPoolWaitMs",
    ];

    private static readonly string[] TextColumns =
    [
        "FileName",
        "DavItemId",
        "HistoryItemId",
        "ProviderStatsJson",
        "ErrorNote",
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var column in TextColumns)
            migrationBuilder.AddColumn<string>(
                name: column,
                table: "ReadSessions",
                type: "TEXT",
                nullable: true);

        foreach (var column in CounterColumns)
            migrationBuilder.AddColumn<int>(
                name: column,
                table: "ReadSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

        migrationBuilder.AddColumn<int>(
            name: "FirstByteMs",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "MaxOffset",
            table: "ReadSessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateIndex(
            name: "IX_ReadSessions_DavItemId_StartedAt",
            table: "ReadSessions",
            columns: ["DavItemId", "StartedAt"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ReadSessions_DavItemId_StartedAt",
            table: "ReadSessions");

        foreach (var column in CounterColumns)
            migrationBuilder.DropColumn(name: column, table: "ReadSessions");

        foreach (var column in TextColumns)
            migrationBuilder.DropColumn(name: column, table: "ReadSessions");

        migrationBuilder.DropColumn(name: "FirstByteMs", table: "ReadSessions");
        migrationBuilder.DropColumn(name: "MaxOffset", table: "ReadSessions");
    }
}
