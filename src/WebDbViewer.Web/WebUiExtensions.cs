using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;
using WebDbViewer.Metadata;
using WebDbViewer.Web.Security;
using WebDbViewer.Web.Services;

namespace WebDbViewer.Web;

/// <summary>DI-регистрация сервисов веб-слоя (UI).</summary>
public static class WebUiExtensions
{
    /// <summary>
    /// Регистрирует сервисы UI: <see cref="ISecretProtector"/> (Data Protection),
    /// <see cref="IMetadataLoader"/> (загрузка снапшотов схем поверх провайдеров) и настройки аутентификации.
    /// </summary>
    public static IServiceCollection AddWebUi(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        services.TryAddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.TryAddSingleton<IMetadataLoader, DbMetadataLoader>();

        return services;
    }
}
