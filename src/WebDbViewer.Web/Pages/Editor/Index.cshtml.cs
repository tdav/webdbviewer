using System.Text;
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

    /// <summary>
    /// Предел файла, который открывается во вкладке редактора. Дамп на сотни мегабайт
    /// незачем гонять в браузер: такой файл выполняют через POST /api/import/sql.
    /// </summary>
    private const long EditorImportLimitBytes = 2 * 1024 * 1024;

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
    /// Вкладка с SQL-скриптом выгрузки таблицы
    /// (hx-get="/editor?handler=ExportTab&amp;ds=…&amp;schema=…&amp;name=…").
    /// Скрипт собирается тем же кодом, что и скачиваемый файл (<see cref="ExportEndpoints"/>),
    /// но с пределом строк: редактор не рассчитан на выгрузку целой таблицы.
    /// Полный файл пользователь получает Ctrl+кликом по той же кнопке дерева.
    /// </summary>
    public async Task<IActionResult> OnGetExportTabAsync(
        int index, Guid ds, string? schema, string? name, string? db,
        [FromServices] IEnumerable<IDdlGenerator> generators,
        [FromServices] IDbProviderRegistry providers,
        CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
        if (index < 1)
            index = 1;

        var config = await _store.GetAsync(ds, ct);
        var tab = new EditorTabVm
        {
            Index = index,
            Title = name is null ? "Экспорт" : $"SQL {name}",
            DefaultDsId = ds == Guid.Empty ? config?.Id : ds,
            DefaultDialect = config?.Kind.ToString().ToLowerInvariant(),
        };

        if (config is null || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name))
            return Partial("_EditorTab", tab with { Content = "-- Не заданы параметры таблицы для выгрузки." });

        var generator = generators.FirstOrDefault(g => g.Kind == config.Kind);
        if (generator is null)
            return Partial("_EditorTab", tab with { Content = $"-- Генератор DDL для «{config.Kind}» не зарегистрирован." });

        try
        {
            // Собственное соединение, как и у endpoint'а экспорта: выгрузка не должна
            // занимать единственное соединение сессии пользователя.
            await using var connection = await _connections.OpenAsync(config, db, ct);

            var provider = providers.Get(config.Kind);
            var target = provider.QuoteIdentifier(schema) + "." + provider.QuoteIdentifier(name);

            var ddl = await DdlText.GetAsync(generator, connection, schema, name, "table", null, ct);

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {target}";
            command.CommandTimeout = config.CommandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(ct);

            var writer = new StringWriter();
            await ExportEndpoints.WriteScriptBodyAsync(
                writer,
                ExportEndpoints.TableHeader(config, schema, name, structure: ddl is not null, data: true),
                ddl, reader, target, config.Kind, provider.QuoteIdentifier,
                ExportLimits.EditorRowLimit, ct);

            return Partial("_EditorTab", tab with { Content = writer.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка выгрузки в SQL: ds={Ds}, {Schema}.{Name}", ds, schema, name);
            return Partial("_EditorTab", tab with
            {
                Content = $"-- Не удалось выгрузить «{schema}.{name}»:\n-- {ex.Message}",
            });
        }
    }

    /// <summary>
    /// Вкладка с содержимым загруженного .sql-файла: файл открывается в редакторе,
    /// но НЕ выполняется — запуск остаётся за пользователем. Для выполнения без
    /// загрузки текста в браузер есть POST /api/import/sql.
    /// </summary>
    public async Task<IActionResult> OnPostImportTabAsync(int index, Guid ds, IFormFile? file, CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
        if (index < 1)
            index = 1;

        var config = await _store.GetAsync(ds, ct);
        var tab = new EditorTabVm
        {
            Index = index,
            Title = file?.FileName ?? "Импорт",
            DefaultDsId = ds == Guid.Empty ? config?.Id : ds,
            DefaultDialect = config?.Kind.ToString().ToLowerInvariant(),
        };

        if (file is null || file.Length == 0)
            return Partial("_EditorTab", tab with { Content = "-- Файл не выбран или пуст." });

        if (file.Length > EditorImportLimitBytes)
            return Partial("_EditorTab", tab with
            {
                Content = $"-- Файл «{file.FileName}» ({file.Length / 1024} КБ) слишком велик для редактора.\n"
                          + $"-- Предел — {EditorImportLimitBytes / 1024} КБ; выполните его через импорт без открытия текста.",
            });

        try
        {
            using var stream = file.OpenReadStream();
            // detectEncodingFromByteOrderMarks: файл из другого инструмента может прийти с BOM.
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(ct);

            return Partial("_EditorTab", tab with { Content = content });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка чтения файла импорта: ds={Ds}, файл={File}", ds, file.FileName);
            return Partial("_EditorTab", tab with { Content = $"-- Не удалось прочитать файл:\n-- {ex.Message}" });
        }
    }

    /// <summary>
    /// Открытие данных таблицы (hx-get="/editor?handler=DataTab&amp;ds=…&amp;schema=…&amp;table=…").
    /// Отдаёт обычную вкладку редактора с готовым SELECT и признаком автозапуска:
    /// строки показывает панель результатов, как у любого другого запроса.
    /// </summary>
    public async Task<IActionResult> OnGetDataTabAsync(
        int index, Guid ds, string? schema, string? table, string? db,
        [FromServices] IDbProviderRegistry providers,
        CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
        if (index < 1)
            index = 1;

        var config = await _store.GetAsync(ds, ct);
        if (config is null || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            return Partial("_EditorTab", new EditorTabVm
            {
                Index = index,
                Title = table ?? "Данные",
                Content = "-- Не удалось открыть данные: датасорс или таблица не заданы.",
            });
        }

        var quote = providers.Get(config.Kind).QuoteIdentifier;
        var database = string.IsNullOrWhiteSpace(db) ? config.Database : db;
        return Partial("_EditorTab", new EditorTabVm
        {
            Index = index,
            Title = $"{schema}.{table}",
            DefaultDsId = ds,
            DefaultDialect = config.Kind.ToString().ToLowerInvariant(),
            // Без предела строк: стрим результатов обрезает выдачу сам и сообщает об этом.
            Content = $"SELECT * FROM {quote(schema)}.{quote(table)}",
            Database = database,
            AutoRun = true,
        });
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

    /// <summary>
    /// База данных, в которой выполнять запрос вкладки; null — база, выбранная в тулбаре.
    /// Нужна открытию данных таблицы: объект может лежать в базе, отличной от текущей.
    /// </summary>
    public string? Database { get; init; }

    /// <summary>true — выполнить содержимое сразу после открытия вкладки (открытие данных таблицы).</summary>
    public bool AutoRun { get; init; }

    public string TabId => $"tab-{Index}";
}
