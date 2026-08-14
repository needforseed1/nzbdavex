using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NzbWebDAV.Database.Migrations;

// Retained as a no-op so databases with this historical migration in their
// history continue to share the same migration sequence. Existing retired
// tables are intentionally left untouched; fresh databases do not create them.
[DbContext(typeof(DavDatabaseContext))]
[Migration("20260602000000_Add-Watchtower-Tables")]
public sealed class AddWatchtowerTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
