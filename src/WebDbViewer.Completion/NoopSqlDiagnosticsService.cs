using WebDbViewer.Core;

namespace WebDbViewer.Completion;

/// <summary>
/// Заглушка диагностики SQL (v0): всегда пусто.
/// Полноценный lint (DELETE/UPDATE без WHERE, синтаксис) — фаза v1.
/// </summary>
public sealed class NoopSqlDiagnosticsService : ISqlDiagnosticsService
{
    public Task<IReadOnlyList<SqlDiagnostic>> AnalyzeAsync(string sql, DbKind dialect, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SqlDiagnostic>>([]);
}
