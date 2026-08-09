using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;
using WebDbViewer.Web.Api;
using WebDbViewer.Web.Services;

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

    /// <summary>
    /// Новая вкладка редактора (hx-get="/editor?handler=Tab&amp;index=N").
    /// С параметрами ds/schema/name/type вкладка открывается с исходником объекта:
    /// текст правится и применяется обычным выполнением скрипта (CREATE OR REPLACE).
    /// </summary>
    public async Task<IActionResult> OnGetTabAsync(
        int index, Guid? ds, string? schema, string? name, string? type, string? db, CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
        if (index < 1)
            index = 1;

        var config = ds is { } dsId ? DataSources.FirstOrDefault(d => d.Id == dsId) : null;
        var source = config is not null && !string.IsNullOrWhiteSpace(schema)
                     && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type)
            ? await LoadSourceAsync(config, schema, name, type, db, ct)
            : null;

        var defaultDs = config ?? DataSources.FirstOrDefault();
        return Partial("_EditorTab", new EditorTabVm
        {
            Index = index,
            DefaultDsId = defaultDs?.Id,
            DefaultDialect = defaultDs?.Kind.ToString().ToLowerInvariant(),
            Title = source is null ? null : name,
            InitialText = source
        });
    }

    /// <summary>Исходник объекта; при ошибке возвращает её текстом комментария — вкладка всё равно открывается.</summary>
    private async Task<string> LoadSourceAsync(
        DataSourceConfig config, string schema, string name, string type, string? db, CancellationToken ct)
    {
        try
        {
            var generator = HttpContext.RequestServices
                .GetServices<IDdlGenerator>()
                .FirstOrDefault(g => g.Kind == config.Kind);
            if (generator is null)
                return $"-- Генератор DDL для «{config.Kind}» не зарегистрирован.\n";

            var session = await _sessions.GetOrCreateAsync(User.Identity?.Name ?? "anonymous", config.Id, db, ct);
            var task = DdlEndpoints.GetDdlTextAsync(generator, session.Connection, schema, name, type, ct);
            if (task is null)
                return $"-- Показ исходника не поддержан для типа «{type}».\n";

            return await task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка получения исходника: {Schema}.{Name} ({Type})", schema, name, type);
            return $"-- Не удалось получить исходник «{schema}.{name}»: {ex.Message}\n";
        }
    }

    /// <summary>
    /// Область выполнения: список баз (если доступны) и схем выбранной базы
    /// (hx-get="/editor?handler=Scope&amp;ds=...&amp;db=...").
    /// </summary>
    public async Task<IActionResult> OnGetScopeAsync(Guid ds, string? db, CancellationToken ct)
    {
        var config = await _store.GetAsync(ds, ct);
        if (config is null)
            return Partial("_EditorScope", new EditorScopeVm { ErrorMessage = "Датасорс не найден." });

        try
        {
            var userName = User.Identity?.Name ?? "anonymous";
            var provider = _providers.Get(config.Kind);
            var withDatabaseLevel = config.AllowAllSchemas && provider.SupportsDatabaseLevel;

            IReadOnlyList<string> databases = [];
            if (withDatabaseLevel)
            {
                var primary = await _sessions.GetOrCreateAsync(userName, ds, null, ct);
                databases = (await provider.GetDatabasesAsync(primary.Connection, includeSystem: false, ct))
                    .Select(n => n.Name)
                    .ToList();
            }

            // База по умолчанию — из настроек подключения; чужую базу принимаем только из её списка.
            var selectedDatabase = databases.Contains(db, StringComparer.Ordinal) ? db : config.Database;
            var session = await _sessions.GetOrCreateAsync(userName, ds, selectedDatabase, ct);

            var schemas = await provider.GetSchemasAsync(session.Connection, includeSystem: false, ct);

            // Датасорс без права на все схемы: показываем только собственную схему подключения.
            var allowedSchemas = await SchemaScope.ResolveAsync(config, session.Connection, ct);
            if (allowedSchemas is not null)
                schemas = schemas.Where(allowedSchemas.Contains).ToList();

            return Partial("_EditorScope", new EditorScopeVm
            {
                Databases = databases,
                SelectedDatabase = selectedDatabase,
                ConnectionDatabase = config.Database,
                Schemas = schemas
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка загрузки списка баз/схем: ds={Ds}, db={Db}", ds, db);
            return Partial("_EditorScope", new EditorScopeVm { ErrorMessage = ex.Message });
        }
    }
}

/// <summary>Модель тулбара «база + схема» для partial _EditorScope.</summary>
public sealed record EditorScopeVm
{
    /// <summary>Базы сервера; пустой список — уровень баз недоступен (Oracle либо подключение без права на все базы).</summary>
    public IReadOnlyList<string> Databases { get; init; } = [];
    public string? SelectedDatabase { get; init; }
    /// <summary>База из настроек подключения — единственная, для которой строится кэш метаданных.</summary>
    public string? ConnectionDatabase { get; init; }
    public IReadOnlyList<string> Schemas { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

/// <summary>Модель новой вкладки редактора для partial _EditorTab.</summary>
public sealed record EditorTabVm
{
    public required int Index { get; init; }
    public Guid? DefaultDsId { get; init; }

    /// <summary>Диалект для CodeMirror: "postgres" | "oracle".</summary>
    public string? DefaultDialect { get; init; }

    /// <summary>Заголовок вкладки; null — обычная вкладка «Запрос N».</summary>
    public string? Title { get; init; }

    /// <summary>Текст, с которым открывается вкладка (исходник объекта).</summary>
    public string? InitialText { get; init; }

    public string TabId => $"tab-{Index}";
}
