using WebDbViewer.Core;
using WebDbViewer.Metadata;

namespace WebDbViewer.Tests.Unit.Metadata;

/// <summary>Тесты persistent-хранилища снапшотов (SQLite roundtrip).</summary>
public class SqliteSnapshotStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"wdbv-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static SchemaSnapshot BuildRichSnapshot() => new()
    {
        SchemaName = "public",
        Tables =
        [
            new TableInfo
            {
                Schema = "public",
                Name = "Orders",   // оригинальный регистр должен сохраниться
                Type = DbObjectType.Table,
                Columns =
                [
                    new ColumnInfo { Name = "id", DataType = "bigint", IsPrimaryKey = true, OrdinalPosition = 1 },
                    new ColumnInfo { Name = "customer_id", DataType = "bigint", IsNullable = true, OrdinalPosition = 2, Comment = "клиент" }
                ],
                ForeignKeys =
                [
                    new ForeignKeyInfo
                    {
                        ConstraintName = "fk_orders_customer",
                        FromSchema = "public", FromTable = "Orders", FromColumns = ["customer_id"],
                        ToSchema = "public", ToTable = "customers", ToColumns = ["id"]
                    }
                ],
                PrimaryKeyColumns = ["id"],
                Comment = "заказы"
            }
        ],
        Routines =
        [
            new RoutineInfo
            {
                Schema = "public", Name = "get_price", Type = DbObjectType.Function,
                ReturnType = "numeric", ArgumentsSignature = "(product_id bigint)"
            }
        ],
        LoadedAt = DateTimeOffset.UtcNow,
        VersionHash = "v1-hash"
    };

    [Fact]
    public async Task SaveAndLoad_Roundtrip_PreservesEverything()
    {
        var store = new SqliteSnapshotStore(_dbPath);
        var ds = Guid.NewGuid();
        var original = BuildRichSnapshot();

        await store.SaveAsync(ds, original, CancellationToken.None);
        var restored = await store.LoadAsync(ds, "public", CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(original.SchemaName, restored.SchemaName);
        Assert.Equal(original.VersionHash, restored.VersionHash);
        Assert.Equal(original.LoadedAt, restored.LoadedAt);

        var table = Assert.Single(restored.Tables);
        Assert.Equal("Orders", table.Name); // оригинальный регистр сохранён
        Assert.Equal(2, table.Columns.Count);
        Assert.Equal("клиент", table.Columns[1].Comment);
        var fk = Assert.Single(table.ForeignKeys);
        Assert.Equal(["customer_id"], fk.FromColumns);
        Assert.Equal(["id"], table.PrimaryKeyColumns);

        var routine = Assert.Single(restored.Routines);
        Assert.Equal("get_price", routine.Name);
        Assert.Equal("(product_id bigint)", routine.ArgumentsSignature);
    }

    [Fact]
    public async Task Load_SchemaName_IsCaseInsensitive()
    {
        var store = new SqliteSnapshotStore(_dbPath);
        var ds = Guid.NewGuid();
        await store.SaveAsync(ds, BuildRichSnapshot(), CancellationToken.None);

        var restored = await store.LoadAsync(ds, "PUBLIC", CancellationToken.None);
        Assert.NotNull(restored);
        Assert.Equal("public", restored.SchemaName);
    }

    [Fact]
    public async Task Save_Upserts_ByDatasourceAndSchema()
    {
        var store = new SqliteSnapshotStore(_dbPath);
        var ds = Guid.NewGuid();
        await store.SaveAsync(ds, BuildRichSnapshot(), CancellationToken.None);
        await store.SaveAsync(ds, BuildRichSnapshot() with { VersionHash = "v2-hash" }, CancellationToken.None);

        var all = await store.LoadAllAsync(CancellationToken.None);
        var single = Assert.Single(all);
        Assert.Equal("v2-hash", single.Snapshot.VersionHash);
    }

    [Fact]
    public async Task LoadAll_ReturnsSnapshotsOfAllDatasources()
    {
        var store = new SqliteSnapshotStore(_dbPath);
        var ds1 = Guid.NewGuid();
        var ds2 = Guid.NewGuid();
        await store.SaveAsync(ds1, BuildRichSnapshot(), CancellationToken.None);
        await store.SaveAsync(ds2, BuildRichSnapshot() with { SchemaName = "audit" }, CancellationToken.None);

        var all = await store.LoadAllAsync(CancellationToken.None);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, p => p.DataSourceId == ds1 && p.Snapshot.SchemaName == "public");
        Assert.Contains(all, p => p.DataSourceId == ds2 && p.Snapshot.SchemaName == "audit");
    }

    [Fact]
    public async Task Delete_RemovesSchema_OrWholeDatasource()
    {
        var store = new SqliteSnapshotStore(_dbPath);
        var ds = Guid.NewGuid();
        await store.SaveAsync(ds, BuildRichSnapshot(), CancellationToken.None);
        await store.SaveAsync(ds, BuildRichSnapshot() with { SchemaName = "audit" }, CancellationToken.None);

        await store.DeleteAsync(ds, "public", CancellationToken.None);
        Assert.Null(await store.LoadAsync(ds, "public", CancellationToken.None));
        Assert.NotNull(await store.LoadAsync(ds, "audit", CancellationToken.None));

        await store.DeleteAsync(ds, null, CancellationToken.None);
        Assert.Empty(await store.LoadAllAsync(CancellationToken.None));
    }
}
