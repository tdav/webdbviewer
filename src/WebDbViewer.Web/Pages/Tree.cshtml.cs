using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core;
using WebDbViewer.Web.Pages.Shared;

namespace WebDbViewer.Web.Pages;

/// <summary>
/// Endpoint дерева навигатора: lazy-раскрытие узлов (hx-get="/tree?ds=...&amp;path=...")
/// и поиск по объектам (hx-get="/tree?handler=Search&amp;ds=...&amp;q=...").
/// Возвращает partial-фрагмент &lt;ul&gt; для HTMX.
/// </summary>
public sealed class TreeModel : PageModel
{
    private readonly IDataSourceStore _store;
    private readonly IDbSessionManager _sessions;
    private readonly IMetadataCache _metadata;
    private readonly ILogger<TreeModel> _logger;

    public TreeModel(
        IDataSourceStore store,
        IDbSessionManager sessions,
        IMetadataCache metadata,
        ILogger<TreeModel> logger)
    {
        _store = store;
        _sessions = sessions;
        _metadata = metadata;
        _logger = logger;
    }

    public IReadOnlyList<TreeNodeVm> Nodes { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    /// <summary>Дочерние узлы: ds — датасорс, path — сегменты через «/» (URL-экранированные), hideSystem — скрывать системные.</summary>
    public async Task OnGetAsync(Guid ds, string? path, bool hideSystem, CancellationToken ct)
    {
        try
        {
            var config = await _store.GetAsync(ds, ct);
            if (config is null)
            {
                ErrorMessage = "Датасорс не найден.";
                return;
            }

            var segments = SplitPath(path);
            var session = await _sessions.GetOrCreateAsync(User.Identity?.Name ?? "anonymous", ds, ct);
            var provider = HttpContext.RequestServices
                .GetRequiredService<IDbProviderRegistry>()
                .Get(config.Kind);

            var children = await provider.GetChildrenAsync(session.Connection, segments, includeSystem: !hideSystem, ct);

            Nodes = children
                .Select(n => new TreeNodeVm
                {
                    DsId = ds,
                    Node = n,
                    Path = AppendSegment(path, n.Name)
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка раскрытия узла дерева: ds={Ds}, path={Path}", ds, path);
            ErrorMessage = $"Ошибка загрузки: {ex.Message}";
        }
    }

    /// <summary>Поиск объектов по кэшу метаданных (trie + camelCase матчинг).</summary>
    public async Task OnGetSearchAsync(Guid ds, string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return; // пустой запрос — пустой результат

        try
        {
            var found = await _metadata.SearchAsync(ds, q.Trim(), limit: 50, ct);
            Nodes = found
                .Select(n => new TreeNodeVm
                {
                    DsId = ds,
                    Node = n,
                    Path = "",
                    NoExpand = true
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка поиска по дереву: ds={Ds}, q={Query}", ds, q);
            ErrorMessage = $"Ошибка поиска: {ex.Message}";
        }
    }

    /// <summary>Разбирает путь узла на сегменты (каждый URL-экранирован).</summary>
    public static IReadOnlyList<string> SplitPath(string? path) =>
        string.IsNullOrEmpty(path)
            ? []
            : path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();

    /// <summary>Добавляет сегмент к пути (с URL-экранированием).</summary>
    public static string AppendSegment(string? path, string segment)
    {
        var escaped = Uri.EscapeDataString(segment);
        return string.IsNullOrEmpty(path) ? escaped : $"{path}/{escaped}";
    }
}
