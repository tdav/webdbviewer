using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;

namespace WebDbViewer.Metadata;

/// <summary>DI-регистрация кэша метаданных.</summary>
public static class MetadataCacheExtensions
{
    /// <summary>
    /// Регистрирует кэш метаданных: IMetadataCache, IMetadataPersistence, ISnapshotStore (SQLite).
    /// Реализацию <see cref="IMetadataLoader"/> должен зарегистрировать Web-слой (поверх IDbProvider).
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="sqlitePath">Путь к файлу SQLite для persistent-снапшотов.</param>
    /// <param name="configure">Дополнительная настройка (TTL и пр.).</param>
    public static IServiceCollection AddMetadataCache(
        this IServiceCollection services,
        string sqlitePath,
        Action<MetadataCacheOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);

        services.AddOptions<MetadataCacheOptions>().Configure(o => o.SqlitePath = sqlitePath);
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<ISnapshotStore, SqliteSnapshotStore>();
        return AddCacheCore(services);
    }

    /// <summary>
    /// Регистрирует кэш метаданных без файлового SQLite-хранилища: реализацию
    /// <see cref="ISnapshotStore"/> должен зарегистрировать вызывающий (например, метабаза PostgreSQL).
    /// </summary>
    public static IServiceCollection AddMetadataCache(
        this IServiceCollection services,
        Action<MetadataCacheOptions>? configure = null)
    {
        services.AddOptions<MetadataCacheOptions>();
        if (configure is not null)
            services.Configure(configure);

        return AddCacheCore(services);
    }

    private static IServiceCollection AddCacheCore(IServiceCollection services)
    {
        services.TryAddSingleton<MetadataCache>();
        services.TryAddSingleton<IMetadataCache>(sp => sp.GetRequiredService<MetadataCache>());
        services.TryAddSingleton<IMetadataPersistence>(sp => sp.GetRequiredService<MetadataCache>());
        return services;
    }
}
