using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;

namespace WebDbViewer.Providers.Postgres;

/// <summary>DI-регистрация провайдера PostgreSQL.</summary>
public static class PostgresProviderExtensions
{
    /// <summary>Регистрирует <see cref="PostgresProvider"/> как <see cref="IDbProvider"/> (singleton).</summary>
    public static IServiceCollection AddPostgresProvider(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProvider, PostgresProvider>());
        return services;
    }
}
