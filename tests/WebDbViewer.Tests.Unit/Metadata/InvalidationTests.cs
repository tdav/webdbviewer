using Microsoft.Extensions.Options;
using WebDbViewer.Metadata;

namespace WebDbViewer.Tests.Unit.Metadata;

/// <summary>Тесты TTL-инвалидации, ручной инвалидации, версионирования и загрузки с диска.</summary>
public class InvalidationTests
{
    private static MetadataCache CreateCache(
        FakeLoader loader,
        InMemorySnapshotStore store,
        TimeSpan? ttl = null,
        TimeProvider? time = null)
        => new(loader, store,
            Options.Create(new MetadataCacheOptions
            {
                Ttl = ttl ?? TimeSpan.FromHours(1),
                ServeStaleWhileRefresh = false // детерминированная синхронная перезагрузка в тестах
            }),
            timeProvider: time);

    /// <summary>Управляемое время для проверки TTL без Task.Delay.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task FreshSnapshot_IsServedFromCache_NoReload()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader();
        var cache = CreateCache(loader, new InMemorySnapshotStore());

        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);

        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public async Task TtlExpired_TriggersReload()
    {
        var ds = Guid.NewGuid();
        var time = new ManualTime();
        var loader = new FakeLoader
        {
            Factory = (_, schema) => TestHelpers.Snapshot(schema, null, TestHelpers.Table(schema, "t1"))
                with { LoadedAt = time.Now }
        };
        var cache = CreateCache(loader, new InMemorySnapshotStore(), ttl: TimeSpan.FromMinutes(5), time);

        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.Equal(1, loader.LoadCount);

        time.Now += TimeSpan.FromMinutes(10); // TTL истёк
        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.Equal(2, loader.LoadCount);

        // Свежезагруженный снапшот снова отдаётся из кэша
        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task ManualInvalidate_Schema_ForcesReload_AndDeletesPersisted()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader();
        var store = new InMemorySnapshotStore();
        var cache = CreateCache(loader, store);

        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.True(store.Contains(ds, "public"));

        await cache.InvalidateAsync(ds, "public", CancellationToken.None);
        Assert.False(store.Contains(ds, "public")); // persistent-снапшот удалён

        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.Equal(2, loader.LoadCount); // перезагрузка после инвалидации
    }

    [Fact]
    public async Task ManualInvalidate_WholeDatasource_DropsAllSchemas()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader();
        var cache = CreateCache(loader, new InMemorySnapshotStore());

        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        await cache.GetSchemaAsync(ds, "audit", CancellationToken.None);
        Assert.Equal(2, (await cache.GetLoadedSchemasAsync(ds, CancellationToken.None)).Count);

        await cache.InvalidateAsync(ds, null, CancellationToken.None);
        Assert.Empty(await cache.GetLoadedSchemasAsync(ds, CancellationToken.None));

        // Поиск по инвалидированному датасорсу пуст
        Assert.Empty(await cache.SearchAsync(ds, "t1", 10, CancellationToken.None));
    }

    [Fact]
    public async Task Reload_WithNewVersionHash_RefreshesSearchIndex()
    {
        var ds = Guid.NewGuid();
        var time = new ManualTime();
        var version = "v1";
        var tableName = "old_table";
        var loader = new FakeLoader();
        loader.Factory = (_, schema) =>
            TestHelpers.Snapshot(schema, version, TestHelpers.Table(schema, tableName))
                with { LoadedAt = time.Now };
        var cache = CreateCache(loader, new InMemorySnapshotStore(), ttl: TimeSpan.FromMinutes(5), time);

        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.Contains(await cache.SearchAsync(ds, "old", 10, CancellationToken.None), n => n.Name == "old_table");

        // DDL изменился: новый hash и новая таблица
        version = "v2";
        tableName = "new_table";
        time.Now += TimeSpan.FromMinutes(10);
        var reloaded = await cache.GetSchemaAsync(ds, "public", CancellationToken.None);

        Assert.Equal("v2", reloaded.VersionHash);
        Assert.Contains(await cache.SearchAsync(ds, "new", 10, CancellationToken.None), n => n.Name == "new_table");
        Assert.Empty(await cache.SearchAsync(ds, "old", 10, CancellationToken.None)); // старый индекс заменён
    }

    [Fact]
    public async Task LoadFromDisk_PopulatesCache_WithoutCallingLoader()
    {
        var ds = Guid.NewGuid();
        var store = new InMemorySnapshotStore();
        await store.SaveAsync(ds,
            TestHelpers.Snapshot("public", "v1", TestHelpers.Table("public", "disk_table")),
            CancellationToken.None);

        var loader = new FakeLoader();
        var cache = CreateCache(loader, store);

        await cache.LoadFromDiskAsync(CancellationToken.None);

        // Снапшот доступен из памяти без обращения к загрузчику (свежий по TTL)
        var snapshot = await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.Equal("public", snapshot.SchemaName);
        Assert.Equal(0, loader.LoadCount);

        // Поисковый индекс построен по данным с диска
        Assert.Contains(await cache.SearchAsync(ds, "disk", 10, CancellationToken.None), n => n.Name == "disk_table");
    }

    [Fact]
    public async Task LoadFromDisk_StaleSnapshot_IsRefreshedInBackground()
    {
        var ds = Guid.NewGuid();
        var store = new InMemorySnapshotStore();
        var stale = TestHelpers.Snapshot("public", "v1", TestHelpers.Table("public", "disk_table"))
            with { LoadedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(1) };
        await store.SaveAsync(ds, stale, CancellationToken.None);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new FakeLoader
        {
            Factory = (_, schema) =>
            {
                gate.TrySetResult();
                return TestHelpers.Snapshot(schema, "v2", TestHelpers.Table(schema, "fresh_table"));
            }
        };
        var cache = CreateCache(loader, store, ttl: TimeSpan.FromMinutes(5));

        await cache.LoadFromDiskAsync(CancellationToken.None);

        // Фоновое обновление устаревшего снапшота стартовало само
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, loader.LoadCount);
    }
}
