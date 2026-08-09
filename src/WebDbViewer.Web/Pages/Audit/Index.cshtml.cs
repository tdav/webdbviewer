using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Pages.Audit;

/// <summary>
/// Страница журнала аудита запросов: фильтры по датам/пользователю/датасорсу.
/// Использует IQueryAuditor, если модуль аудита зарегистрирован; иначе показывает заглушку.
/// </summary>
public sealed class AuditIndexModel : PageModel
{
    private readonly IDataSourceStore _store;
    private readonly IQueryAuditor? _auditor; // может отсутствовать до подключения модуля аудита
    private readonly ILogger<AuditIndexModel> _logger;

    public AuditIndexModel(IDataSourceStore store, ILogger<AuditIndexModel> logger, IQueryAuditor? auditor = null)
    {
        _store = store;
        _logger = logger;
        _auditor = auditor;
    }

    public IReadOnlyList<DataSourceConfig> DataSources { get; private set; } = [];
    public string DefaultFrom => DateTime.Today.AddDays(-7).ToString("yyyy-MM-dd");
    public string DefaultTo => DateTime.Today.ToString("yyyy-MM-dd");

    public async Task OnGetAsync(CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
    }

    /// <summary>Выборка журнала (hx-get="/audit?handler=Query"). Возвращает partial с таблицей.</summary>
    public async Task<IActionResult> OnGetQueryAsync(
        DateTime? from, DateTime? to, string? userName, Guid? dataSourceId, CancellationToken ct)
    {
        if (_auditor is null)
        {
            // IQueryAuditor не зарегистрирован (нет AddPostgresMetaStore) — заглушка
            return Content(
                "<div class=\"alert alert-info\">Модуль аудита ещё не подключён. " +
                "Журнал станет доступен после регистрации IQueryAuditor.</div>",
                "text/html");
        }

        try
        {
            var fromUtc = new DateTimeOffset((from ?? DateTime.Today.AddDays(-7)).Date, TimeSpan.Zero);
            var toUtc = new DateTimeOffset((to ?? DateTime.Today).Date.AddDays(1), TimeSpan.Zero);

            var entries = await _auditor.QueryAsync(
                fromUtc, toUtc,
                string.IsNullOrWhiteSpace(userName) ? null : userName.Trim(),
                dataSourceId == Guid.Empty ? null : dataSourceId,
                limit: 500, ct);

            return Partial("_AuditTable", entries);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка выборки журнала аудита");
            return Content(
                $"<div class=\"alert alert-error\">Ошибка выборки журнала: {System.Net.WebUtility.HtmlEncode(ex.Message)}</div>",
                "text/html");
        }
    }
}
