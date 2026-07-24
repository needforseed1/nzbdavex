using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260723000000_Add-Submission-Source")]
public partial class AddSubmissionSource : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SubmissionSource",
            table: "QueueItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SubmissionSource",
            table: "HistoryItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_HistoryItems_SubmissionSource_Category_CreatedAt",
            table: "HistoryItems",
            columns: new[] { "SubmissionSource", "Category", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_HistoryItems_SubmissionSource_Category_CreatedAt",
            table: "HistoryItems");

        migrationBuilder.DropColumn(name: "SubmissionSource", table: "QueueItems");
        migrationBuilder.DropColumn(name: "SubmissionSource", table: "HistoryItems");
    }
}
