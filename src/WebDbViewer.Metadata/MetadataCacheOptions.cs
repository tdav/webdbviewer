namespace WebDbViewer.Metadata;

/// <summary>Настройки кэша метаданных.</summary>
public sealed class MetadataCacheOptions
{
    /// <summary>Путь к файлу SQLite для persistent-снапшотов.</summary>
    public string SqlitePath { get; set; } = "metadata-cache.db";

    /// <summary>TTL снапшота схемы: по истечении снапшот считается устаревшим и перезагружается.</summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// true — устаревший снапшот отдаётся сразу, а обновление идёт в фоне (stale-while-revalidate);
    /// false — при устаревании вызывающий ждёт синхронной перезагрузки.
    /// Для снапшотов, поднятых с диска, фоновое обновление применяется всегда.
    /// </summary>
    public bool ServeStaleWhileRefresh { get; set; }
}
