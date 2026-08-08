using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;

namespace WebDbViewer.Providers.Oracle;

/// <summary>DI-регистрация провайдера Oracle.</summary>
public static class OracleProviderExtensions
{
    /// <summary>Регистрирует <see cref="OracleProvider"/> как <see cref="IDbProvider"/> (singleton).</summary>
    public static IServiceCollection AddOracleProvider(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDbProvider, OracleProvider>());
        return services;
    }
}
