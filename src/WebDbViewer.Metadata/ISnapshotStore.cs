using WebDbViewer.Core;

namespace WebDbViewer.Metadata;

/// <summary>Сохранённый на диске снапшот схемы вместе с идентификатором датасорса.</summary>
public sealed record PersistedSnapshot(Guid DataSourceId, SchemaSnapshot Snapshot);

/// <summary>Persistent-хранилище снапшотов схем (реализация — метабаза PostgreSQL).</summary>
public interface ISnapshotStore
{
    /// <summary>Сохраняет/обновляет снапшот (upsert по паре датасорс+схема).</summary>
    Task SaveAsync(Guid dataSourceId, SchemaSnapshot snapshot, CancellationToken ct);

    /// <summary>Загружает все сохранённые снапшоты (для старта приложения).</summary>
    Task<IReadOnlyList<PersistedSnapshot>> LoadAllAsync(CancellationToken ct);

    /// <summary>Загружает один снапшот; null — если не найден. Имя схемы сравнивается без учёта регистра.</summary>
    Task<SchemaSnapshot?> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct);

    /// <summary>Удаляет снапшоты: конкретной схемы или всего датасорса (schema = null).</summary>
    Task DeleteAsync(Guid dataSourceId, string? schema, CancellationToken ct);
}
