using WebDbViewer.Core;

namespace WebDbViewer.Metadata;

/// <summary>
/// Загрузчик снапшота схемы из живой БД.
/// Реализуется Web-слоем поверх <see cref="IDbProvider"/> (открытие соединения + LoadSchemaSnapshotAsync).
/// Кэш метаданных знает только этот интерфейс и не зависит от провайдеров.
/// </summary>
public interface IMetadataLoader
{
    /// <summary>Загружает полный снапшот схемы указанного датасорса.</summary>
    Task<SchemaSnapshot> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct);
}
