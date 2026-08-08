using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WebDbViewer.Web.Api;

/// <summary>DI-регистрация подсистемы стриминга результатов запросов.</summary>
public static class ResultStreamingExtensions
{
    /// <summary>
    /// Регистрирует <see cref="ExecutionRegistry"/> — реестр выполняющихся запросов
    /// (executionId → RunningQuery) для SSE-стрима и отмены.
    /// Должен вызываться в Program.cs до <c>app.MapQueryApi()</c>.
    /// </summary>
    public static IServiceCollection AddResultStreaming(this IServiceCollection services)
    {
        services.TryAddSingleton<ExecutionRegistry>();
        return services;
    }
}
