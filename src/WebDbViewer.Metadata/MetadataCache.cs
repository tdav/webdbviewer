using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebDbViewer.Core;
using WebDbViewer.Metadata.Search;

namespace WebDbViewer.Metadata;

/// <summary>
/// Кэш метаданных per-datasource: снапшоты схем в памяти, single-flight загрузка,
/// TTL-инвалидация, поиск (trie + humps), persistent-снапшот через <see cref="ISnapshotStore"/>.
/// Учёт регистра: ключи словарей — OrdinalIgnoreCase, оригинальные имена сохраняются;
/// квотирование идентификаторов — задача провайдера.
/// </summary>
public sealed class MetadataCache : IMetadataCache, IMetadataPersistence
{
    /// <summary>
    /// Запись схемы: держатель single-flight-задачи загрузки.
    /// Lazy(ExecutionAndPublication) гарантирует один запуск загрузки на все параллельные запросы.
    /// </summary>
    private sealed class SchemaEntry
    {
        private Lazy<Task<SchemaSnapshot>> _lazy;

        /// <summary>Флаг «фоновое обновление уже идёт» (0/1, Interlocked).</summary>
        public int RefreshInFlight;

        public SchemaEntry(Lazy<Task<SchemaSnapshot>> lazy) => _lazy = lazy;

        public Lazy<Task<SchemaSnapshot>> Lazy => Volatile.Read(ref _lazy);

        /// <summary>Атомарная замена single-flight-задачи (CAS: заменяем только наблюдавшуюся).</summary>
        public bool TrySwap(Lazy<Task<SchemaSnapshot>> expected, Lazy<Task<SchemaSnapshot>> next)
            => ReferenceEquals(Interlocked.CompareExchange(ref _lazy, next, expected), expected);

        /// <summary>Замена на уже завершённый результат (после фонового обновления).</summary>
        public void Replace(SchemaSnapshot snapshot)
        {
            var completed = Task.FromResult(snapshot);
            Volatile.Write(ref _lazy, new Lazy<Task<SchemaSnapshot>>(() => completed));
        }
    }

    /// <summary>Кэш одного датасорса: схемы + поисковые индексы (ключи — без учёта регистра).</summary>
    private sealed class DatasourceCache
    {
        public readonly ConcurrentDictionary<string, SchemaEntry> Schemas = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, SchemaSearchIndex> Indexes = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly ConcurrentDictionary<Guid, DatasourceCache> _caches = new();
    private readonly IMetadataLoader _loader;
    private readonly ISnapshotStore _store;
    private readonly MetadataCacheOptions _options;
    private readonly ILogger<MetadataCache> _logger;
    private readonly TimeProvider _time;

    public MetadataCache(
        IMetadataLoader loader,
        ISnapshotStore store,
        IOptions<MetadataCacheOptions> options,
        ILogger<MetadataCache>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _loader = loader;
        _store = store;
        _options = options.Value;
        _logger = logger ?? NullLogger<MetadataCache>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<SchemaSnapshot> GetSchemaAsync(Guid dataSourceId, string schemaName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        var ds = _caches.GetOrAdd(dataSourceId, _ => new DatasourceCache());

        var entry = ds.Schemas.GetOrAdd(schemaName, s => new SchemaEntry(NewLoadLazy(dataSourceId, s, previous: null)));
        var lazy = entry.Lazy;
        var task = lazy.Value;

        // Предыдущая загрузка упала/отменена — одна повторная попытка (single-flight на новую задачу)
        if (task.IsFaulted || task.IsCanceled)
        {
            var fresh = NewLoadLazy(dataSourceId, schemaName, previous: null);
            lazy = entry.TrySwap(lazy, fresh) ? fresh : entry.Lazy;
            task = lazy.Value;
        }

        var snapshot = await task.WaitAsync(ct).ConfigureAwait(false);

        if (IsFresh(snapshot))
            return snapshot;

        // Снапшот устарел (TTL)
        if (_options.ServeStaleWhileRefresh || Volatile.Read(ref entry.RefreshInFlight) == 1)
        {
            // Отдаём устаревший, обновление — в фоне (или уже идёт)
            TryStartBackgroundRefresh(dataSourceId, schemaName, entry, snapshot);
            return snapshot;
        }

        // Синхронная перезагрузка: заменяем single-flight-задачу и ждём
        var reload = NewLoadLazy(dataSourceId, schemaName, previous: snapshot);
        var current = entry.TrySwap(lazy, reload) ? reload : entry.Lazy;
        return await current.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SchemaSnapshot>> GetLoadedSchemasAsync(Guid dataSourceId, CancellationToken ct)
    {
        var result = new List<SchemaSnapshot>();
        if (_caches.TryGetValue(dataSourceId, out var ds))
        {
            foreach (var entry in ds.Schemas.Values)
            {
                var lazy = entry.Lazy;
                if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
                    result.Add(lazy.Value.Result);
            }
        }
        return Task.FromResult<IReadOnlyList<SchemaSnapshot>>(result);
    }

    public async Task WarmupAsync(Guid dataSourceId, IReadOnlyList<string> schemas, CancellationToken ct)
    {
        // Прогреваем схемы параллельно; ошибка одной схемы не прерывает остальные
        var tasks = schemas.Select(async schema =>
        {
            try
            {
                await GetSchemaAsync(dataSourceId, schema, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Прогрев схемы {Schema} датасорса {DataSourceId} не удался", schema, dataSourceId);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DbObjectNode>> SearchAsync(Guid dataSourceId, string query, int limit, CancellationToken ct)
    {
        var scored = new List<ScoredNode>();
        if (!string.IsNullOrWhiteSpace(query) && _caches.TryGetValue(dataSourceId, out var ds))
        {
            foreach (var index in ds.Indexes.Values)
            {
                ct.ThrowIfCancellationRequested();
                index.Search(query, scored);
            }
        }

        IReadOnlyList<DbObjectNode> result = scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Node.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit <= 0 ? int.MaxValue : limit)
            .Select(s => s.Node)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task InvalidateAsync(Guid dataSourceId, string? schemaName, CancellationToken ct)
    {
        if (_caches.TryGetValue(dataSourceId, out var ds))
        {
            if (schemaName is null)
            {
                ds.Schemas.Clear();
                ds.Indexes.Clear();
            }
            else
            {
                ds.Schemas.TryRemove(schemaName, out _);
                ds.Indexes.TryRemove(schemaName, out _);
            }
        }

        // Удаляем и persistent-снапшот: следующий запрос загрузит свежие метаданные из БД
        try
        {
            await _store.DeleteAsync(dataSourceId, schemaName, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить persistent-снапшот {Schema} датасорса {DataSourceId}", schemaName, dataSourceId);
        }
    }

    /// <summary>
    /// Загрузка persistent-снапшотов с диска при старте приложения.
    /// Устаревшие (по TTL) снапшоты сразу отправляются на фоновое обновление.
    /// </summary>
    public async Task LoadFromDiskAsync(CancellationToken ct)
    {
        IReadOnlyList<PersistedSnapshot> persisted;
        try
        {
            persisted = await _store.LoadAllAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать persistent-снапшоты метаданных");
            return;
        }

        foreach (var (dataSourceId, snapshot) in persisted)
        {
            ct.ThrowIfCancellationRequested();
            var ds = _caches.GetOrAdd(dataSourceId, _ => new DatasourceCache());
            var entry = new SchemaEntry(CompletedLazy(snapshot));
            // Не затираем уже загруженные в память снапшоты
            if (!ds.Schemas.TryAdd(snapshot.SchemaName, entry))
                continue;
            ds.Indexes[snapshot.SchemaName] = SchemaSearchIndex.Build(snapshot);

            if (!IsFresh(snapshot))
                TryStartBackgroundRefresh(dataSourceId, snapshot.SchemaName, entry, snapshot);
        }
    }

    /// <summary>Снапшот свежий, если не истёк TTL.</summary>
    private bool IsFresh(SchemaSnapshot snapshot)
        => _time.GetUtcNow() - snapshot.LoadedAt < _options.Ttl;

    private static Lazy<Task<SchemaSnapshot>> CompletedLazy(SchemaSnapshot snapshot)
    {
        var completed = Task.FromResult(snapshot);
        return new Lazy<Task<SchemaSnapshot>>(() => completed);
    }

    /// <summary>
    /// Создаёт single-flight-задачу загрузки. LazyThreadSafetyMode.ExecutionAndPublication
    /// гарантирует ровно один вызов загрузчика на все параллельные запросы.
    /// Токен вызывающего не передаётся: загрузка общая, отмена одного запроса её не прерывает.
    /// </summary>
    private Lazy<Task<SchemaSnapshot>> NewLoadLazy(Guid dataSourceId, string schemaName, SchemaSnapshot? previous)
        => new(() => Task.Run(() => LoadCoreAsync(dataSourceId, schemaName, previous, CancellationToken.None)),
               LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Загрузка снапшота через IMetadataLoader + сохранение на диск + перестройка поискового индекса.
    /// Версионирование: если VersionHash не изменился, индекс не перестраивается.
    /// </summary>
    private async Task<SchemaSnapshot> LoadCoreAsync(Guid dataSourceId, string schemaName, SchemaSnapshot? previous, CancellationToken ct)
    {
        var startedAt = _time.GetTimestamp();
        var snapshot = await _loader.LoadAsync(dataSourceId, schemaName, ct).ConfigureAwait(false);
        _logger.LogDebug("Интроспекция схемы {Schema} датасорса {DataSourceId}: {ElapsedMs} мс, таблиц {Tables}",
            schemaName, dataSourceId,
            (int)_time.GetElapsedTime(startedAt).TotalMilliseconds, snapshot.Tables.Count);

        if (snapshot.LoadedAt == default)
            snapshot = snapshot with { LoadedAt = _time.GetUtcNow() };

        var ds = _caches.GetOrAdd(dataSourceId, _ => new DatasourceCache());
        var unchanged = previous?.VersionHash is not null
                        && snapshot.VersionHash is not null
                        && string.Equals(previous.VersionHash, snapshot.VersionHash, StringComparison.Ordinal)
                        && ds.Indexes.ContainsKey(schemaName);
        if (!unchanged)
            ds.Indexes[schemaName] = SchemaSearchIndex.Build(snapshot);

        try
        {
            await _store.SaveAsync(dataSourceId, snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить persistent-снапшот {Schema} датасорса {DataSourceId}", schemaName, dataSourceId);
        }

        return snapshot;
    }

    /// <summary>Запускает фоновое обновление устаревшего снапшота (не более одного одновременно).</summary>
    private void TryStartBackgroundRefresh(Guid dataSourceId, string schemaName, SchemaEntry entry, SchemaSnapshot stale)
    {
        if (Interlocked.CompareExchange(ref entry.RefreshInFlight, 1, 0) != 0)
            return; // обновление уже идёт

        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await LoadCoreAsync(dataSourceId, schemaName, stale, CancellationToken.None).ConfigureAwait(false);
                entry.Replace(fresh);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Фоновое обновление схемы {Schema} датасорса {DataSourceId} не удалось", schemaName, dataSourceId);
            }
            finally
            {
                Volatile.Write(ref entry.RefreshInFlight, 0);
            }
        });
    }
}
