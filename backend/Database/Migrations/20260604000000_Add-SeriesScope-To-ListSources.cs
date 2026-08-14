using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NzbWebDAV.Database.Migrations;

// Historical no-op retained for migration-sequence compatibility.
[DbContext(typeof(DavDatabaseContext))]
[Migration("20260604000000_Add-SeriesScope-To-ListSources")]
public sealed class AddSeriesScopeToListSources : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
    protected override void Down(MigrationBuilder migrationBuilder) { }
}
