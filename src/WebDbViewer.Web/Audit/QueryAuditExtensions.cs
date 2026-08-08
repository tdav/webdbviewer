using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Audit;

/// <summary>DI-регистрация аудита запросов.</summary>
public static class QueryAuditExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IQueryAuditor"/> поверх отдельной базы SQLite (по умолчанию ./audit.db).
    /// TODO(владелец Program.cs): после builder.Services.AddQueryAudit() добавить app.MapAuditApi()
    /// (endpoint GET /audit/query — HTML-partial журнала для HTMX-страницы аудита).
    /// </summary>
    public static IServiceCollection AddQueryAudit(this IServiceCollection services, Action<AuditOptions>? configure = null)
    {
        services.AddOptions<AuditOptions>();
        if (configure is not null)
            services.Configure(configure);
        services.TryAddSingleton<IQueryAuditor, SqliteQueryAuditor>();
        return services;
    }
}
