using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Pages.Connections;

/// <summary>Страница управления подключениями (датасорсами): список, создание, редактирование, проверка, удаление.</summary>
public sealed class ConnectionsIndexModel : PageModel
{
    private readonly IDataSourceStore _store;
    private readonly IDbProviderRegistry _providers;
    private readonly ISecretProtector _protector;
    private readonly ILogger<ConnectionsIndexModel> _logger;

    public ConnectionsIndexModel(
        IDataSourceStore store,
        IDbProviderRegistry providers,
        ISecretProtector protector,
        ILogger<ConnectionsIndexModel> logger)
    {
        _store = store;
        _providers = providers;
        _protector = protector;
        _logger = logger;
    }

    public IReadOnlyList<DataSourceConfig> DataSources { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        DataSources = await _store.GetAllAsync(ct);
    }

    /// <summary>Форма создания/редактирования (hx-get="/connections?handler=Form[&amp;id=...]").</summary>
    public async Task<IActionResult> OnGetFormAsync(Guid? id, CancellationToken ct)
    {
        if (id is null)
            return Partial("_Form", new ConnectionFormVm());

        var config = await _store.GetAsync(id.Value, ct);
        if (config is null)
            return Partial("_Form", new ConnectionFormVm());

        return Partial("_Form", new ConnectionFormVm
        {
            Id = config.Id,
            Name = config.Name,
            Kind = config.Kind,
            Host = config.Host,
            Port = config.Port,
            Database = config.Database,
            Username = config.Username,
            UseSsl = config.UseSsl,
            ReadOnly = config.ReadOnly,
            IsProduction = config.IsProduction,
            AllowAllSchemas = config.AllowAllSchemas
        });
    }

    /// <summary>Сохранение подключения (hx-post="/connections?handler=Save"). Возвращает обновлённый список.</summary>
    public async Task<IActionResult> OnPostSaveAsync(ConnectionFormInput input, CancellationToken ct)
    {
        var existing = input.Id is { } id && id != Guid.Empty ? await _store.GetAsync(id, ct) : null;

        // Пароль: новый шифруем Data Protection; пустой при редактировании — оставляем прежний
        string? protectedPassword = existing?.ProtectedPassword;
        if (!string.IsNullOrEmpty(input.Password))
            protectedPassword = _protector.Protect(input.Password);

        var config = new DataSourceConfig
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = input.Name?.Trim() ?? "Без названия",
            Kind = input.Kind,
            Host = input.Host?.Trim() ?? "localhost",
            Port = input.Port > 0 ? input.Port : DefaultPort(input.Kind),
            Database = input.Database?.Trim() ?? "",
            Username = input.Username?.Trim() ?? "",
            ProtectedPassword = protectedPassword,
            UseSsl = input.UseSsl,
            ReadOnly = input.ReadOnly,
            IsProduction = input.IsProduction,
            AllowAllSchemas = input.AllowAllSchemas
        };

        await _store.SaveAsync(config, ct);
        _logger.LogInformation("Сохранено подключение «{Name}» ({Id})", config.Name, config.Id);

        DataSources = await _store.GetAllAsync(ct);
        return Partial("_List", DataSources);
    }

    /// <summary>Проверка подключения (hx-post="/connections?handler=Test"). Возвращает partial с результатом.</summary>
    public async Task<IActionResult> OnPostTestAsync(ConnectionFormInput input, CancellationToken ct)
    {
        try
        {
            var password = input.Password ?? "";
            // При редактировании без ввода пароля используем сохранённый
            if (string.IsNullOrEmpty(password) && input.Id is { } id && id != Guid.Empty)
            {
                var existing = await _store.GetAsync(id, ct);
                if (existing?.ProtectedPassword is { Length: > 0 } stored)
                    password = _protector.Unprotect(stored);
            }

            var config = new DataSourceConfig
            {
                Id = input.Id ?? Guid.NewGuid(),
                Name = input.Name ?? "test",
                Kind = input.Kind,
                Host = input.Host?.Trim() ?? "localhost",
                Port = input.Port > 0 ? input.Port : DefaultPort(input.Kind),
                Database = input.Database?.Trim() ?? "",
                Username = input.Username?.Trim() ?? "",
                UseSsl = input.UseSsl,
                ConnectTimeoutSeconds = 10
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            var provider = _providers.Get(config.Kind);
            var version = await provider.TestConnectionAsync(config, password, timeout.Token);
            return Partial("_TestResult", new TestResultVm { Success = true, Message = version });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Проверка подключения не удалась: {Host}:{Port}", input.Host, input.Port);
            return Partial("_TestResult", new TestResultVm { Success = false, Message = ex.Message });
        }
    }

    /// <summary>Удаление подключения (hx-post="/connections?handler=Delete&amp;id=..."). Возвращает обновлённый список.</summary>
    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        await _store.DeleteAsync(id, ct);
        _logger.LogInformation("Удалено подключение {Id}", id);

        DataSources = await _store.GetAllAsync(ct);
        return Partial("_List", DataSources);
    }

    /// <summary>Порт по умолчанию для типа СУБД.</summary>
    private static int DefaultPort(DbKind kind) => kind == DbKind.Oracle ? 1521 : 5432;
}

/// <summary>Данные формы подключения (POST).</summary>
public sealed class ConnectionFormInput
{
    public Guid? Id { get; set; }
    public string? Name { get; set; }
    public DbKind Kind { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Database { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseSsl { get; set; }
    public bool ReadOnly { get; set; }
    public bool IsProduction { get; set; }
    /// <summary>Чекбокс «Все схемы / базы данных»: снятый флажок ограничивает навигатор схемой подключения.</summary>
    public bool AllowAllSchemas { get; set; }
}

/// <summary>Модель формы подключения для partial _Form.</summary>
public sealed record ConnectionFormVm
{
    public Guid? Id { get; init; }
    public string Name { get; init; } = "";
    public DbKind Kind { get; init; } = DbKind.Postgres;
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Database { get; init; } = "";
    public string Username { get; init; } = "";
    public bool UseSsl { get; init; }
    public bool ReadOnly { get; init; }
    public bool IsProduction { get; init; }
    /// <summary>По умолчанию новое подключение видит все схемы — как и раньше.</summary>
    public bool AllowAllSchemas { get; init; } = true;
}

/// <summary>Результат проверки подключения для partial _TestResult.</summary>
public sealed record TestResultVm
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
}
