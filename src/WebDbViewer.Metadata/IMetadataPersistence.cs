namespace WebDbViewer.Metadata;

/// <summary>
/// Управление persistent-снапшотом кэша метаданных.
/// Web-слой вызывает <see cref="LoadFromDiskAsync"/> при старте приложения:
/// снапшоты поднимаются с диска в память, устаревшие обновляются в фоне.
/// </summary>
public interface IMetadataPersistence
{
    /// <summary>Загружает все сохранённые снапшоты с диска в память (при старте приложения).</summary>
    Task LoadFromDiskAsync(CancellationToken ct);
}
