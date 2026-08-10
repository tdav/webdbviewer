using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core;
using WebDbViewer.Web.Pages.Shared;
using WebDbViewer.Web.Services;

namespace WebDbViewer.Web.Pages;

/// <summary>
/// Endpoint дерева навигатора: lazy-раскрытие узлов (hx-get="/tree?ds=...&amp;path=...")
/// и поиск по объектам (hx-get="/tree?handler=Search&amp;ds=...&amp;q=...").
/// Возвращает partial-фрагмент &lt;ul&gt; для HTMX.
/// </summary>
public sealed class TreeModel : PageModel
{
    private readonly IDataSourceStore _store;
    // Навигатор читает каталог собственными короткоживущими соединениями: раскрытие узла
    // не должно ни ждать выполняющийся в сессии запрос, ни конкурировать с ним за соединение.
    private readonly IDbConnectionFactory _connections;
    private readonly IMetadataCache _metadata;
    private readonly ILogger<TreeModel> _logger;

    public TreeModel(
        IDataSourceStore store,
        IDbConnectionFactory connections,
        IMetadataCache metadata,
        ILogger<TreeModel> logger)
    {
        _store = store;
        _connections = connections;
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
                ErrorMessage = "DataSource не найден.";
                return;
            }

            var segments = SplitPath(path);
            var provider = HttpContext.RequestServices
                .GetRequiredService<IDbProviderRegistry>()
                .Get(config.Kind);

            // Уровень «базы данных» — только когда подключению разрешены все базы и СУБД их различает.
            var withDatabaseLevel = config.AllowAllSchemas && provider.SupportsDatabaseLevel;

            // Первый сегмент пути — имя базы; в провайдер уходит путь без него.
            var database = withDatabaseLevel && segments.Count > 0 ? segments[0] : null;
            var schemaPath = withDatabaseLevel ? segments.Skip(1).ToList() : segments;

            await using var connection = await _connections.OpenAsync(config, database, ct);

            if (withDatabaseLevel && segments.Count == 0)
            {
                var databases = await provider.GetDatabasesAsync(connection, includeSystem: !hideSystem, ct);
                // Без kind: логотип СУБД несёт только корень датасорса, вложенные
                // базы одного и того же сервера получают обычный цилиндр.
                Nodes = ToNodes(ds, path, databases, readOnly: config.ReadOnly);
                return;
            }

            // Датасорс без права на все схемы: корень фильтруем, вглубь чужой схемы не пускаем.
            var allowedSchemas = await SchemaScope.ResolveAsync(config, connection, ct);
            if (schemaPath.Count > 0 && !SchemaScope.IsAllowed(allowedSchemas, schemaPath[0]))
            {
                ErrorMessage = "Схема недоступна: подключение ограничено собственной схемой.";
                return;
            }

            var children = await provider.GetChildrenAsync(connection, schemaPath, includeSystem: !hideSystem, ct);
            if (schemaPath.Count == 0)
                children = SchemaScope.Filter(allowedSchemas, children);

            // Без уровня баз (Oracle) схемы стоят сразу под датасорсом — рисуем их
            // цилиндром, чтобы второй уровень дерева выглядел одинаково в обеих СУБД.
            Nodes = ToNodes(ds, path, children, database, config.ReadOnly,
                asDatabase: !withDatabaseLevel && schemaPath.Count == 0);
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

            // Поиск не должен обходить ограничение области видимости схем.
            var config = await _store.GetAsync(ds, ct);
            if (config is { AllowAllSchemas: false })
            {
                await using var connection = await _connections.OpenAsync(config, null, ct);
                var allowedSchemas = await SchemaScope.ResolveAsync(config, connection, ct);
                found = SchemaScope.Filter(allowedSchemas, found);
            }

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

    /// <summary>Преобразует объекты БД в модели узлов дерева (путь к детям = путь родителя + имя узла).</summary>
    private static IReadOnlyList<TreeNodeVm> ToNodes(
        Guid ds, string? parentPath, IReadOnlyList<DbObjectNode> nodes, string? database = null,
        bool readOnly = false, bool asDatabase = false) =>
        nodes
            .Select(n => new TreeNodeVm
            {
                DsId = ds,
                Node = n,
                Database = database,
                ReadOnly = readOnly,
                AsDatabase = asDatabase,
                Path = AppendSegment(parentPath, n.Name)
            })
            .ToList();

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
