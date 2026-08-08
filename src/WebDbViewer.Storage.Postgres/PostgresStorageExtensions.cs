using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebDbViewer.Core;
using WebDbViewer.Core.Security;
using WebDbViewer.Metadata;

namespace WebDbViewer.Storage.Postgres;

/// <summary>DI-регистрация метабазы приложения на PostgreSQL.</summary>
public static class PostgresStorageExtensions
{
    /// <summary>
    /// Регистрирует ВСЕ хранилища настроек поверх одной базы PostgreSQL:
    /// <see cref="IDataSourceStore"/>, <see cref="IUserStore"/>, <see cref="ISnapshotStore"/>,
    /// <see cref="IQueryAuditor"/> и репозиторий ключей Data Protection.
    /// Вызывать ДО AddDbSessions/AddMetadataCache — те регистрируют файловые реализации через TryAdd.
    /// </summary>
    public static IServiceCollection AddPostgresMetaStore(
        this IServiceCollection services,
        Action<PostgresStorageOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<PostgresStorageOptions>().Configure(configure);

        services.TryAddSingleton<PostgresMetaStore>();
        services.TryAddSingleton<PostgresXmlRepository>();

        services.TryAddSingleton<IDataSourceStore, PostgresDataSourceStore>();
        services.TryAddSingleton<ISnapshotStore, PostgresSnapshotStore>();
        services.TryAddSingleton<IQueryAuditor, PostgresQueryAuditor>();
        services.TryAddSingleton<PostgresUserStore>();
        services.TryAddSingleton<IUserStore>(sp => sp.GetRequiredService<PostgresUserStore>());

        return services;
    }

    /// <summary>
    /// Переносит кольцо ключей Data Protection в метабазу PostgreSQL.
    /// Без этого зашифрованные пароли датасорсов остаются привязанными к локальной папке ./keys.
    /// </summary>
    public static IDataProtectionBuilder PersistKeysToPostgres(this IDataProtectionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(options =>
                options.XmlRepository = sp.GetRequiredService<PostgresXmlRepository>()));

        return builder;
    }

    /// <summary>
    /// Готовит метабазу к работе при старте приложения: создаёт схему и таблицы,
    /// при необходимости выполняет одноразовый импорт ключей Data Protection из папки.
    /// </summary>
    public static async Task InitializePostgresMetaStoreAsync(
        this IServiceProvider services,
        CancellationToken ct = default)
    {
        var meta = services.GetRequiredService<PostgresMetaStore>();
        await meta.EnsureCreatedAsync(ct).ConfigureAwait(false);

        var options = services.GetRequiredService<IOptions<PostgresStorageOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.ImportKeysFromDirectory))
            return;

        var repository = services.GetRequiredService<PostgresXmlRepository>();
        var imported = await repository
            .ImportFromDirectoryAsync(options.ImportKeysFromDirectory, ct)
            .ConfigureAwait(false);

        if (imported > 0)
        {
            services.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(PostgresStorageExtensions))
                .LogInformation(
                    "Импортировано ключей Data Protection из «{Directory}»: {Count}",
                    options.ImportKeysFromDirectory, imported);
        }
    }
}
