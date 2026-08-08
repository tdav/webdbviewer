using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;
using WebDbViewer.Parsing;

namespace WebDbViewer.Completion;

/// <summary>DI-регистрация SQL-интеллекта: сплит, автодополнение, диагностика.</summary>
public static class SqlIntelliSenseExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IStatementSplitter"/> (лексерный сплит с dollar-quoting и PL/SQL-блоками),
    /// <see cref="ICompletionEngine"/> / <see cref="ISemanticCompletionEngine"/> (antlr4-c3 + кэш
    /// метаданных + семантическая модель v1) и заглушку <see cref="ISqlDiagnosticsService"/>.
    /// Требует зарегистрированный <see cref="IMetadataCache"/> (AddMetadataCache).
    /// </summary>
    public static IServiceCollection AddSqlIntelliSense(this IServiceCollection services)
    {
        services.TryAddSingleton<IStatementSplitter, StatementSplitter>();
        services.TryAddSingleton<CompletionEngine>();
        services.TryAddSingleton<ICompletionEngine>(sp => sp.GetRequiredService<CompletionEngine>());
        // Расширенный контракт v1 (перегрузка с CompletionOptions: автоалиасы через параметр запроса).
        services.TryAddSingleton<ISemanticCompletionEngine>(sp => sp.GetRequiredService<CompletionEngine>());
        services.TryAddSingleton<ISqlDiagnosticsService, NoopSqlDiagnosticsService>();
        return services;
    }
}
