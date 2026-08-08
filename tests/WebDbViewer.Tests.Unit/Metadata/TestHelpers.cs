using System.Collections.Concurrent;
using WebDbViewer.Core;
using WebDbViewer.Metadata;

namespace WebDbViewer.Tests.Unit.Metadata;

/// <summary>Вспомогательные фабрики снапшотов и фейки для тестов кэша метаданных.</summary>
internal static class TestHelpers
{
    public static TableInfo Table(string schema, string name, string[]? columns = null, bool withFk = false)
    {
        var cols = (columns ?? ["id"])
            .Select((c, i) => new ColumnInfo
            {
                Name = c,
                DataType = "text",
                OrdinalPosition = i + 1
            })
            .ToList();
        return new TableInfo
        {
            Schema = schema,
            Name = name,
            Type = DbObjectType.Table,
            Columns = cols,
            ForeignKeys = withFk
                ?
                [
                    new ForeignKeyInfo
                    {
                        ConstraintName = $"fk_{name}",
                        FromSchema = schema,
                        FromTable = name,
                        FromColumns = ["id"],
                        ToSchema = schema,
                        ToTable = "parent",
                        ToColumns = ["id"]
                    }
                ]
                : []
        };
    }

    public static SchemaSnapshot Snapshot(string schema, string? versionHash = null, params TableInfo[] tables)
        => new()
        {
            SchemaName = schema,
            Tables = tables,
            LoadedAt = DateTimeOffset.UtcNow,
            VersionHash = versionHash
        };
}

/// <summary>Фейковый загрузчик: считает вызовы, умеет задерживать загрузку и подменять снапшоты.</summary>
internal sealed class FakeLoader : IMetadataLoader
{
    private int _loadCount;
    public int LoadCount => Volatile.Read(ref _loadCount);

    /// <summary>Ворота: если заданы, загрузка ждёт открытия.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Фабрика снапшота по (датасорс, схема).</summary>
    public Func<Guid, string, SchemaSnapshot> Factory { get; set; } =
        (_, schema) => TestHelpers.Snapshot(schema, null, TestHelpers.Table(schema, "t1"));

    /// <summary>Если задано — загрузка бросает это исключение.</summary>
    public Exception? Throw { get; set; }

    public ConcurrentBag<(Guid DataSourceId, string Schema)> Calls { get; } = [];

    public async Task<SchemaSnapshot> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct)
    {
        Interlocked.Increment(ref _loadCount);
        Calls.Add((dataSourceId, schema));
        if (Gate is not null)
            await Gate.Task.WaitAsync(ct);
        if (Throw is not null)
            throw Throw;
        return Factory(dataSourceId, schema);
    }
}

/// <summary>In-memory реализация ISnapshotStore для тестов без файловой системы.</summary>
internal sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly ConcurrentDictionary<(Guid, string), SchemaSnapshot> _data =
        new(EqualityComparer<(Guid, string)>.Create(
            (a, b) => a.Item1 == b.Item1 && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase),
            key => HashCode.Combine(key.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Item2))));

    public int SaveCount;

    public Task SaveAsync(Guid dataSourceId, SchemaSnapshot snapshot, CancellationToken ct)
    {
        Interlocked.Increment(ref SaveCount);
        _data[(dataSourceId, snapshot.SchemaName)] = snapshot;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersistedSnapshot>> LoadAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PersistedSnapshot>>(
            _data.Select(kv => new PersistedSnapshot(kv.Key.Item1, kv.Value)).ToList());

    public Task<SchemaSnapshot?> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct)
        => Task.FromResult(_data.TryGetValue((dataSourceId, schema), out var s) ? s : null);

    public Task DeleteAsync(Guid dataSourceId, string? schema, CancellationToken ct)
    {
        foreach (var key in _data.Keys.Where(k =>
                     k.Item1 == dataSourceId &&
                     (schema is null || string.Equals(k.Item2, schema, StringComparison.OrdinalIgnoreCase))))
        {
            _data.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    public bool Contains(Guid dataSourceId, string schema) => _data.ContainsKey((dataSourceId, schema));
}
