using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebDbViewer.Core.Sessions;

/// <summary>DI-регистрация подсистемы stateful-сессий БД и хранилища датасорсов.</summary>
public static class DbSessionsExtensions
{
    /// <summary>
    /// Регистрирует: <see cref="IDbProviderRegistry"/>, <see cref="IDbSessionManager"/> (с лимитами и TTL),
    /// фоновый <see cref="SessionSweeper"/> и файловое хранилище датасорсов <see cref="DataSourceFileStore"/>.
    /// Провайдеры СУБД регистрируются отдельно (AddPostgresProvider/AddOracleProvider),
    /// <see cref="ISecretProtector"/> — модулем Web (Data Protection); без него допускаются только датасорсы без пароля.
    /// </summary>
    public static IServiceCollection AddDbSessions(this IServiceCollection services, Action<DbSessionOptions>? configure = null)
    {
        services.AddOptions<DbSessionOptions>();
        if (configure is not null)
            services.Configure(configure);

        services.TryAddSingleton<IDbProviderRegistry, DbProviderRegistry>();

        services.TryAddSingleton<IDataSourceStore>(sp =>
            new DataSourceFileStore(sp.GetRequiredService<IOptions<DbSessionOptions>>().Value.DataSourcesFilePath));

        // Короткоживущие соединения для чтения метаданных — мимо сессий пользователя.
        services.TryAddSingleton<IDbConnectionFactory>(sp => new DbConnectionFactory(
            sp.GetRequiredService<IDataSourceStore>(),
            sp.GetRequiredService<IDbProviderRegistry>(),
            sp.GetService<ISecretProtector>()));

        services.TryAddSingleton(sp => new DbSessionManager(
            sp.GetRequiredService<IDataSourceStore>(),
            sp.GetRequiredService<IDbProviderRegistry>(),
            sp.GetService<ISecretProtector>(),
            sp.GetRequiredService<IOptions<DbSessionOptions>>(),
            sp.GetService<ILogger<DbSessionManager>>()));
        services.TryAddSingleton<IDbSessionManager>(sp => sp.GetRequiredService<DbSessionManager>());

        services.AddHostedService<SessionSweeper>();
        return services;
    }
}
