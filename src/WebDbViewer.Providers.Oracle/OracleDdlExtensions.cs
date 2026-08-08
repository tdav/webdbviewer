using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core.Ddl;

namespace WebDbViewer.Providers.Oracle;

/// <summary>DI-регистрация генератора DDL Oracle.</summary>
public static class OracleDdlExtensions
{
    /// <summary>Регистрирует <see cref="OracleDdlGenerator"/> как <see cref="IDdlGenerator"/> (singleton).</summary>
    public static IServiceCollection AddOracleDdl(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDdlGenerator, OracleDdlGenerator>());
        return services;
    }
}
