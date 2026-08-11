using Microsoft.Extensions.Options;
using WebDbViewer.Core;
using WebDbViewer.Metadata;

namespace WebDbViewer.Tests.Unit.Completion;

public class MetadataRefreshTests
{
    /// <summary>Загрузчик со счётчиком: показывает, ходил ли кэш в базу заново.</summary>
    private sealed class CountingLoader : IMetadataLoader
    {
        public int Calls;

        public Task<SchemaSnapshot> LoadAsync(Guid dataSourceId, string? database, string schemaName, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SchemaSnapshot
            {
                SchemaName = schemaName,
                Tables = [],
                LoadedAt = DateTimeOffset.UtcNow,
                VersionHash = "v" + Calls,
            });
        }
    }

    private sealed class NullSnapshotStore : ISnapshotStore
    {
        public Task SaveAsync(Guid dataSourceId, SchemaSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid dataSourceId, string? schemaName, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PersistedSnapshot>> LoadAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersistedSnapshot>>([]);
        public Task<SchemaSnapshot?> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct) =>
            Task.FromResult<SchemaSnapshot?>(null);
    }

    [Fact]
    public async Task Invalidate_ForcesReload()
    {
        var loader = new CountingLoader();
        var cache = new MetadataCache(loader, new NullSnapshotStore(), Options.Create(new MetadataCacheOptions()));
        var dsId = Guid.NewGuid();

        await cache.GetSchemaAsync(dsId, null, "public", CancellationToken.None);
        await cache.GetSchemaAsync(dsId, null, "public", CancellationToken.None);
        Assert.Equal(1, loader.Calls); // второй запрос обслужен из кэша

        await cache.InvalidateAsync(dsId, null, "public", CancellationToken.None);
        await cache.GetSchemaAsync(dsId, null, "public", CancellationToken.None);

        Assert.Equal(2, loader.Calls);
    }

    /// <summary>Разные базы одного датасорса кэшируются независимо: снапшот db1 не глушит db2 и наоборот.</summary>
    [Fact]
    public async Task DifferentDatabases_AreCachedSeparately()
    {
        var loader = new CountingLoader();
        var cache = new MetadataCache(loader, new NullSnapshotStore(), Options.Create(new MetadataCacheOptions()));
        var dsId = Guid.NewGuid();

        await cache.GetSchemaAsync(dsId, "db1", "public", CancellationToken.None);
        await cache.GetSchemaAsync(dsId, "db2", "public", CancellationToken.None);
        Assert.Equal(2, loader.Calls); // разные базы — разные загрузки

        await cache.InvalidateAsync(dsId, "db1", null, CancellationToken.None);
        await cache.GetSchemaAsync(dsId, "db1", "public", CancellationToken.None);
        Assert.Equal(3, loader.Calls); // db1 инвалидирован — перезагрузка

        await cache.GetSchemaAsync(dsId, "db2", "public", CancellationToken.None);
        Assert.Equal(3, loader.Calls); // db2 не затронут инвалидацией db1 — из кэша
    }
}
