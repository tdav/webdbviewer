using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;

namespace WebDbViewer.Metadata;

/// <summary>DI-регистрация кэша метаданных.</summary>
public static class MetadataCacheExtensions
{
    /// <summary>
    /// Регистрирует кэш метаданных: IMetadataCache, IMetadataPersistence.
    /// Реализацию <see cref="ISnapshotStore"/> должен зарегистрировать вызывающий
    /// (например, метабаза PostgreSQL), а <see cref="IMetadataLoader"/> — Web-слой (поверх IDbProvider).
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <param name="configure">Дополнительная настройка (TTL и пр.).</param>
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
