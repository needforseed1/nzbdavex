using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Migrations;

namespace NzbWebDAV.Tests.Database;

public class RetiredFeaturePersistenceTests
{
    [Fact]
    public void HistoricalFeatureMigrationsRemainAsNoOps()
    {
        Migration[] migrations =
        [
            new AddWatchtowerTables(),
            new AddSeriesScopeToListSources(),
            new AddUpdatedAtUnixIndexToWantedItems(),
        ];

        Assert.All(migrations, migration =>
        {
            Assert.Empty(migration.UpOperations);
            Assert.Empty(migration.DownOperations);
        });
    }

    [Fact]
    public void MixedStableIdMigrationNoLongerChangesRetiredTables()
    {
        var migration = new AddStableConfigIds();

        Assert.DoesNotContain(migration.UpOperations, IsRetiredTableOperation);
        Assert.DoesNotContain(migration.DownOperations, IsRetiredTableOperation);
    }

    [Fact]
    public void CurrentModelIgnoresRetiredTables()
    {
        using var context = new DavDatabaseContext();
        var mappedTables = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(table => table is not null)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ListSources", mappedTables);
        Assert.DoesNotContain("WantedItems", mappedTables);
    }

    private static bool IsRetiredTableOperation(MigrationOperation operation) => operation switch
    {
        TableOperation table => table.Name is "ListSources" or "WantedItems",
        ColumnOperation column => column.Table is "ListSources" or "WantedItems",
        CreateIndexOperation index => index.Table is "ListSources" or "WantedItems",
        DropIndexOperation index => index.Table is "ListSources" or "WantedItems",
        _ => false,
    };
}
