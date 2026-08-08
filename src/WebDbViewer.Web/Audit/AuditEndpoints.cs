using System.Text;
using System.Text.Encodings.Web;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Audit;

/// <summary>
/// Endpoint журнала аудита: GET /audit/query → HTML-partial (таблица) для HTMX.
/// Параметры: from, to (ISO-8601), user, ds (Guid), limit.
/// </summary>
public static class AuditEndpoints
{
    private const int MaxSqlPreviewLength = 300;

    public static IEndpointRouteBuilder MapAuditApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/audit/query", GetAuditTableAsync).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> GetAuditTableAsync(
        HttpContext http,
        IQueryAuditor auditor,
        IServiceProvider services,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? user = null,
        Guid? ds = null,
        int limit = 200)
    {
        var fromValue = from ?? DateTimeOffset.UtcNow.AddDays(-1);
        var toValue = to ?? DateTimeOffset.UtcNow;

        var entries = await auditor.QueryAsync(fromValue, toValue, user, ds, limit, ct);

        // Имена датасорсов для отображения (store может быть не зарегистрирован — тогда Guid).
        var dsNames = new Dictionary<Guid, string>();
        var store = services.GetService<IDataSourceStore>();
        if (store is not null)
        {
            try
            {
                foreach (var config in await store.GetAllAsync(ct))
                    dsNames[config.Id] = config.Name;
            }
            catch
            {
                // Отображение имён — необязательное удобство.
            }
        }

        return Results.Content(RenderTable(entries, dsNames), "text/html; charset=utf-8");
    }

    /// <summary>HTML-partial журнала аудита (на русском) для вставки HTMX.</summary>
    private static string RenderTable(IReadOnlyList<AuditEntry> entries, IReadOnlyDictionary<Guid, string> dsNames)
    {
        var h = HtmlEncoder.Default;
        var sb = new StringBuilder(4096);

        if (entries.Count == 0)
        {
            sb.Append("<p class=\"audit-empty\">Записей аудита за выбранный период не найдено.</p>");
            return sb.ToString();
        }

        sb.Append("<table class=\"audit-table\">");
        sb.Append("<thead><tr>")
          .Append("<th>Время</th>")
          .Append("<th>Пользователь</th>")
          .Append("<th>Датасорс</th>")
          .Append("<th>SQL</th>")
          .Append("<th>Длительность</th>")
          .Append("<th>Строк</th>")
          .Append("<th>Статус</th>")
          .Append("<th>IP</th>")
          .Append("</tr></thead><tbody>");

        foreach (var e in entries)
        {
            var dsName = dsNames.TryGetValue(e.DataSourceId, out var name)
                ? name
                : e.DataSourceId.ToString("N")[..8];
            var sqlPreview = e.SqlText.Length > MaxSqlPreviewLength
                ? e.SqlText[..MaxSqlPreviewLength] + "…"
                : e.SqlText;

            sb.Append("<tr class=\"").Append(e.Success ? "audit-ok" : "audit-error").Append("\">");
            sb.Append("<td>").Append(h.Encode(e.StartedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"))).Append("</td>");
            sb.Append("<td>").Append(h.Encode(e.UserName)).Append("</td>");
            sb.Append("<td>").Append(h.Encode(dsName)).Append("</td>");
            sb.Append("<td><code title=\"").Append(h.Encode(e.SqlText)).Append("\">")
              .Append(h.Encode(sqlPreview)).Append("</code></td>");
            sb.Append("<td>").Append(FormatDuration(e.Duration)).Append("</td>");
            sb.Append("<td>").Append(e.RowsAffected?.ToString() ?? "—").Append("</td>");
            sb.Append("<td>").Append(e.Success
                ? "<span class=\"badge badge-success\">Успех</span>"
                : "<span class=\"badge badge-danger\" title=\"" + h.Encode(e.ErrorMessage ?? "") + "\">Ошибка</span>");
            sb.Append("</td>");
            sb.Append("<td>").Append(h.Encode(e.ClientIp ?? "—")).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");
        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.##") + " с"
            : duration.TotalMilliseconds.ToString("0") + " мс";
}
