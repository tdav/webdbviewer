using Microsoft.Extensions.Options;
using WebDbViewer.Metadata;

namespace WebDbViewer.Tests.Unit.Metadata;

/// <summary>Тесты single-flight: параллельные запросы одной схемы приводят ровно к одной загрузке.</summary>
public class SingleFlightTests
{
    private static MetadataCache CreateCache(FakeLoader loader, InMemorySnapshotStore store)
        => new(loader, store, Options.Create(new MetadataCacheOptions { Ttl = TimeSpan.FromHours(1) }));

    [Fact]
    public async Task ParallelRequests_SameSchema_LoadOnce()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var cache = CreateCache(loader, new InMemorySnapshotStore());

        // 20 параллельных запросов зависают на воротах загрузчика
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => cache.GetSchemaAsync(ds, "public", CancellationToken.None)))
            .ToArray();

        // Даём всем запросам дойти до ожидания
        await Task.Delay(100);
        loader.Gate.SetResult();

        var snapshots = await Task.WhenAll(tasks);

        Assert.Equal(1, loader.LoadCount); // ровно одна загрузка
        Assert.All(snapshots, s => Assert.Same(snapshots[0], s)); // все получили один и тот же снапшот
    }

    [Fact]
    public async Task DifferentSchemas_LoadSeparately()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader();
        var cache = CreateCache(loader, new InMemorySnapshotStore());

        await Task.WhenAll(
            cache.GetSchemaAsync(ds, "public", CancellationToken.None),
            cache.GetSchemaAsync(ds, "audit", CancellationToken.None));

        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task SchemaName_IsCaseInsensitive_SingleLoad()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader();
        var cache = CreateCache(loader, new InMemorySnapshotStore());

        await cache.GetSchemaAsync(ds, "PUBLIC", CancellationToken.None);
        await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        await cache.GetSchemaAsync(ds, "Public", CancellationToken.None);

        Assert.Equal(1, loader.LoadCount); // ключи словаря — OrdinalIgnoreCase
    }

    [Fact]
    public async Task FailedLoad_IsRetried_OnNextRequest()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader { Throw = new InvalidOperationException("нет соединения") };
        var cache = CreateCache(loader, new InMemorySnapshotStore());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetSchemaAsync(ds, "public", CancellationToken.None));

        // Ошибка не кэшируется навсегда: следующий запрос загружает заново
        loader.Throw = null;
        var snapshot = await cache.GetSchemaAsync(ds, "public", CancellationToken.None);
        Assert.Equal("public", snapshot.SchemaName);
        Assert.Equal(2, loader.LoadCount);
    }

    [Fact]
    public async Task Warmup_LoadsAllSchemas_IgnoringFailures()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader
        {
            Factory = (_, schema) => schema == "broken"
                ? throw new InvalidOperationException("схема сломана")
                : TestHelpers.Snapshot(schema, null, TestHelpers.Table(schema, "t"))
        };
        var cache = CreateCache(loader, new InMemorySnapshotStore());

        await cache.WarmupAsync(ds, ["public", "broken", "audit"], CancellationToken.None);

        var loaded = await cache.GetLoadedSchemasAsync(ds, CancellationToken.None);
        Assert.Equal(2, loaded.Count);
        Assert.DoesNotContain(loaded, s => s.SchemaName == "broken");
    }
}
