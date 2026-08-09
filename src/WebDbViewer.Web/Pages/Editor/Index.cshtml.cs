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
    // Списки баз/схем и тексты DDL — интроспекция каталога: читаются вне сессии пользователя,
    // чтобы не конкурировать с выполняющимся в ней запросом за единственное соединение.
    private readonly IDbConnectionFactory _connections;
    private readonly IDbProviderRegistry _providers;
    private readonly ILogger<EditorIndexModel> _logger;

    public EditorIndexModel(
        IDataSourceStore store,
        IDbConnectionFactory connections,
        IDbProviderRegistry providers,
        ILogger<EditorIndexModel> logger)
    {
        _store = store;
        _connections = connections;
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
        var defaultDs = DataSources.FirstOrDefault();
        return Partial("_EditorTab", new EditorTabVm
        {
            Index = index,
            DefaultDsId = defaultDs?.Id,
            DefaultDialect = defaultDs?.Kind.ToString().ToLowerInvariant()
        });
    }

    /// <summary>
    /// Вкладка редактора со скриптом объекта
    /// (hx-get="/editor?handler=DdlTab&amp;ds=…&amp;schema=…&amp;name=…&amp;type=…&amp;script=ddl|drop").
    /// qualifier уточняет объект среди одноимённых: таблица-владелец для триггера, правила
    /// и политики RLS, сигнатура аргументов для перегруженной функции.
    /// Скрипт только открывается в редакторе — выполняет его пользователь.
    /// Ошибка не ломает вкладку: её текст приходит комментарием в теле редактора.
    /// </summary>
    public async Task<IActionResult> OnGetDdlTabAsync(
        int index, Guid ds, string? schema, string? name, string? type, string? qualifier, string? db, string? script,
        [FromServices] IEnumerable<IDdlGenerator> generators,
        CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
        if (index < 1)
            index = 1;

        var isDrop = string.Equals(script, "drop", StringComparison.OrdinalIgnoreCase);
        var config = await _store.GetAsync(ds, ct);
        var tab = new EditorTabVm
        {
            Index = index,
            Title = isDrop ? $"DROP {name}" : name ?? "DDL",
            DefaultDsId = ds == Guid.Empty ? config?.Id : ds,
            DefaultDialect = config?.Kind.ToString().ToLowerInvariant(),
        };

        if (config is null || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            return Partial("_EditorTab", tab with { Content = "-- Не заданы параметры объекта для получения скрипта." });

        var generator = generators.FirstOrDefault(g => g.Kind == config.Kind);
        if (generator is null)
            return Partial("_EditorTab", tab with { Content = $"-- Генератор DDL для «{config.Kind}» не зарегистрирован." });

        try
        {
            await using var connection = await _connections.OpenAsync(config, db, ct);
            var text = isDrop
                ? await DdlText.GetDropAsync(generator, connection, schema, name, type, qualifier, ct)
                : await DdlText.GetAsync(generator, connection, schema, name, type, qualifier, ct);

            if (text is null)
                return Partial("_EditorTab", tab with { Content = $"-- Неизвестный тип объекта: «{type}»." });

            // Скрипт удаления снабжается предупреждением: он не выполняется сам,
            // но открывается в редакторе, где Ctrl+Enter находится в одном нажатии.
            var content = isDrop
                ? $"-- Удаление объекта «{schema}.{name}». Скрипт НЕ выполнен.\n"
                  + "-- Проверьте зависимости; CASCADE при необходимости допишите вручную.\n\n"
                  + text
                : text;

            return Partial("_EditorTab", tab with { Content = content });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка получения скрипта: ds={Ds}, {Schema}.{Name} ({Type}, {Script})", ds, schema, name, type, script);
            return Partial("_EditorTab", tab with { Content = $"-- Не удалось получить скрипт «{schema}.{name}»:\n-- {ex.Message}" });
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
            var provider = _providers.Get(config.Kind);
            var withDatabaseLevel = config.AllowAllSchemas && provider.SupportsDatabaseLevel;

            IReadOnlyList<string> databases = [];
            if (withDatabaseLevel)
            {
                await using var primary = await _connections.OpenAsync(config, null, ct);
                databases = (await provider.GetDatabasesAsync(primary, includeSystem: false, ct))
                    .Select(n => n.Name)
                    .ToList();
            }

            // База по умолчанию — из настроек подключения; чужую базу принимаем только из её списка.
            var selectedDatabase = databases.Contains(db, StringComparer.Ordinal) ? db : config.Database;
            await using var connection = await _connections.OpenAsync(config, selectedDatabase, ct);

            var schemas = await provider.GetSchemasAsync(connection, includeSystem: false, ct);

            // Датасорс без права на все схемы: показываем только собственную схему подключения.
            var allowedSchemas = await SchemaScope.ResolveAsync(config, connection, ct);
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

    /// <summary>Подпись на кнопке вкладки; null — «Запрос N».</summary>
    public string? Title { get; init; }

    /// <summary>Начальный текст редактора (например, DDL объекта); null — пустая вкладка.</summary>
    public string? Content { get; init; }

    public string TabId => $"tab-{Index}";
}
