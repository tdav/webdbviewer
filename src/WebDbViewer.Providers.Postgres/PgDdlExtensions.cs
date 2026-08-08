using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core.Ddl;

namespace WebDbViewer.Providers.Postgres;

/// <summary>DI-регистрация генератора DDL PostgreSQL.</summary>
public static class PgDdlExtensions
{
    /// <summary>Регистрирует <see cref="PgDdlGenerator"/> как <see cref="IDdlGenerator"/> (singleton).</summary>
    public static IServiceCollection AddPostgresDdl(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDdlGenerator, PgDdlGenerator>());
        return services;
    }
}
