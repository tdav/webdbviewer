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

        public Task<SchemaSnapshot> LoadAsync(Guid dataSourceId, string schemaName, CancellationToken ct)
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

        await cache.GetSchemaAsync(dsId, "public", CancellationToken.None);
        await cache.GetSchemaAsync(dsId, "public", CancellationToken.None);
        Assert.Equal(1, loader.Calls); // второй запрос обслужен из кэша

        await cache.InvalidateAsync(dsId, "public", CancellationToken.None);
        await cache.GetSchemaAsync(dsId, "public", CancellationToken.None);

        Assert.Equal(2, loader.Calls);
    }
}
