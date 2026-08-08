using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Pages.Editor;

/// <summary>Главная страница SQL-редактора: навигатор, вкладки редакторов, панель результатов.</summary>
public sealed class EditorIndexModel : PageModel
{
    private readonly IDataSourceStore _store;
    private readonly IDbSessionManager _sessions;
    private readonly IDbProviderRegistry _providers;
    private readonly ILogger<EditorIndexModel> _logger;

    public EditorIndexModel(
        IDataSourceStore store,
        IDbSessionManager sessions,
        IDbProviderRegistry providers,
        ILogger<EditorIndexModel> logger)
    {
        _store = store;
        _sessions = sessions;
        _providers = providers;
        _logger = logger;
    }

    public IReadOnlyList<DataSourceConfig> DataSources { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
    }

    /// <summary>Новая вкладка редактора (hx-get="/editor?handler=Tab&amp;index=N").</summary>
    public async Task<IActionResult> OnGetTabAsync(int index, CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
        if (index < 1)
            index = 1;
        return Partial("_EditorTab", new EditorTabVm
        {
            Index = index,
            DefaultDsId = DataSources.FirstOrDefault()?.Id
        });
    }

    /// <summary>Список схем датасорса для выпадающего списка (hx-get="/editor?handler=Schemas&amp;ds=...").</summary>
    public async Task<IActionResult> OnGetSchemasAsync(Guid ds, CancellationToken ct)
    {
        try
        {
            var config = await _store.GetAsync(ds, ct);
            if (config is null)
                return Content("<option value=\"\">— датасорс не найден —</option>", "text/html");

            var session = await _sessions.GetOrCreateAsync(User.Identity?.Name ?? "anonymous", ds, ct);
            var provider = _providers.Get(config.Kind);
            var schemas = await provider.GetSchemasAsync(session.Connection, includeSystem: false, ct);

            var html = new System.Text.StringBuilder("<option value=\"\">— по умолчанию —</option>");
            foreach (var schema in schemas)
            {
                var encoded = System.Net.WebUtility.HtmlEncode(schema);
                html.Append($"<option value=\"{encoded}\">{encoded}</option>");
            }
            return Content(html.ToString(), "text/html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка загрузки списка схем: ds={Ds}", ds);
            return Content("<option value=\"\">— ошибка загрузки схем —</option>", "text/html");
        }
    }
}

/// <summary>Модель новой вкладки редактора для partial _EditorTab.</summary>
public sealed record EditorTabVm
{
    public required int Index { get; init; }
    public Guid? DefaultDsId { get; init; }
    public string TabId => $"tab-{Index}";
}
