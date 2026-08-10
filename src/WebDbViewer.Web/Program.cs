using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using DbViewer.App.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.WebEncoders;
using Serilog;
using Serilog.Events;
using WebDbViewer.Completion;
using WebDbViewer.Core.Sessions;
using WebDbViewer.Metadata;
using WebDbViewer.Providers.Oracle;
using WebDbViewer.Providers.Postgres;
using WebDbViewer.Storage.Postgres;
using WebDbViewer.Web.Api;
using WebDbViewer.Web.Audit;
using WebDbViewer.Web.Security;

// ---------- Кодировка консоли: сообщения логов на русском ----------
// Консоль Windows по умолчанию в OEM-кодировке (CP866), из-за чего кириллица превращается в мусор.
// При перенаправлении вывода (docker logs, CI) поток уже UTF-8 — трогать его не нужно.
if (!Console.IsOutputRedirected)
{
    try
    {
        Console.OutputEncoding = Encoding.UTF8;
    }
    catch (IOException)
    {
        // Консоли нет (служба Windows) — вывод всё равно никто не читает.
    }
}

// ---------- Bootstrap-логгер: ловит ошибки старта (например, недоступную метабазу) до сборки DI ----------
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Запуск WebDbViewer");

    var builder = WebApplication.CreateBuilder(args);

    // ---------- Serilog: полная конфигурация из appsettings (секция Serilog) ----------
    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "WebDbViewer"));

    // ---------- Метабаза: ВСЕ настройки приложения (датасорсы, пользователи, ключи, снапшоты, аудит) ----------
    // Регистрируется первой: AddDbSessions/AddMetadataCache используют TryAdd
    // и не перезапишут уже зарегистрированные Postgres-реализации.
    var metaConnectionString = builder.Configuration.GetConnectionString("MetaStore")
        ?? throw new InvalidOperationException(
            "Не задана строка подключения ConnectionStrings:MetaStore к метабазе PostgreSQL.");

    builder.Services.AddPostgresMetaStore(options =>
    {
        options.ConnectionString = metaConnectionString;
        options.Schema = builder.Configuration.GetValue("MetaStore:Schema", "webdbviewer")!;

        // Одноразовый перенос ранее созданного кольца ключей с диска — иначе пароли
        // датасорсов, зашифрованные старыми ключами, перестанут расшифровываться.
        var legacyKeys = Path.Combine(builder.Environment.ContentRootPath, "keys");
        if (Directory.Exists(legacyKeys))
            options.ImportKeysFromDirectory = legacyKeys;
    });

    // ---------- HTML/JS-энкодеры: не экранировать кириллицу ----------
    // По умолчанию HtmlEncoder разрешает только Basic Latin, поэтому любой русский текст,
    // выведенный через @Model.X, превращался в «&#x41D;&#x435;...» — страница раздувается
    // в разы, а HTML нечитаем при отладке. Страницы отдаются в UTF-8, экранирование не нужно.
    builder.Services.Configure<WebEncoderOptions>(options =>
        options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

    // JSON minimal API (сообщения об ошибках): по умолчанию кириллица уходит в \uXXXX-эскейпы.
    // Create(UnicodeRanges.All) пропускает её как есть, но сохраняет экранирование <, >, & —
    // в отличие от UnsafeRelaxedJsonEscaping, ответ остаётся безопасным при вставке в HTML.
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));

    // ---------- Razor Pages: авторизация по умолчанию для всех страниц, логин — анонимный ----------
    builder.Services.AddRazorPages(options =>
    {
        options.Conventions.AuthorizeFolder("/");
        options.Conventions.AllowAnonymousToPage("/Login");
    });

    // ---------- Data Protection: кольцо ключей хранится в метабазе PostgreSQL ----------
    builder.Services.AddDataProtection()
        .SetApplicationName("WebDbViewer")
        .PersistKeysToPostgres();

    // ---------- Cookie-аутентификация (учётные записи — в метабазе, пароль — хэш PBKDF2) ----------
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.LogoutPath = "/login";
            options.AccessDeniedPath = "/login";
            options.Cookie.Name = "WebDbViewer.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(
                builder.Configuration.GetValue("Auth:CookieLifetimeMinutes", 480));
            options.SlidingExpiration = true;
        });
    builder.Services.AddAuthorization();

    // ---------- Серверная сессия (состояние вкладок редактора, выбранный датасорс и т.п.) ----------
    builder.Services.AddDistributedMemoryCache();
    builder.Services.AddSession(options =>
    {
        options.Cookie.Name = "WebDbViewer.Session";
        options.Cookie.HttpOnly = true;
        options.IdleTimeout = TimeSpan.FromHours(8);
    });

    // ---------- Antiforgery: HTMX шлёт токен заголовком (hx-headers в _Layout) ----------
    builder.Services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

    // ---------- Модули приложения ----------
    builder.Services.AddPostgresProvider();
    builder.Services.AddOracleProvider();

    // Генераторы DDL (вкладка «DDL объекта», /api/ddl) и DML (inline-редактирование грида).
    // Регистрация обязательна: без неё обработчики отвечают «генератор … не зарегистрирован».
    builder.Services.AddPostgresDdl();
    builder.Services.AddOracleDdl();
    builder.Services.AddPostgresDmlGeneration();
    builder.Services.AddOracleDmlGeneration();

    // Датасорсы и снапшоты метаданных берутся из метабазы (IDataSourceStore/ISnapshotStore уже зарегистрированы)
    builder.Services.AddDbSessions();
    builder.Services.AddMetadataCache();

    // SQL-интеллект: IStatementSplitter, ICompletionEngine, ISqlDiagnosticsService
    builder.Services.AddSqlIntelliSense();
    // Реестр выполняющихся запросов (SSE-стрим, отмена)
    builder.Services.AddResultStreaming();

    // Сервисы веб-слоя: ISecretProtector (Data Protection), IMetadataLoader, AuthOptions
    builder.Services.AddWebUi(builder.Configuration);

    var app = builder.Build();

    // ---------- Конвейер ----------
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/error");
    }

    // ---------- Статика: явный charset=utf-8 ----------
    // StaticFileMiddleware отдаёт «text/css» и «text/javascript» без charset, и браузер
    // определяет кодировку эвристикой (для css/js — по кодировке страницы-владельца).
    // В файлах есть кириллица, поэтому кодировку объявляем явно.
    var contentTypeProvider = new FileExtensionContentTypeProvider();
    contentTypeProvider.Mappings[".js"] = "text/javascript; charset=utf-8";
    contentTypeProvider.Mappings[".mjs"] = "text/javascript; charset=utf-8";
    contentTypeProvider.Mappings[".css"] = "text/css; charset=utf-8";
    contentTypeProvider.Mappings[".json"] = "application/json; charset=utf-8";
    contentTypeProvider.Mappings[".svg"] = "image/svg+xml; charset=utf-8";
    contentTypeProvider.Mappings[".txt"] = "text/plain; charset=utf-8";
    contentTypeProvider.Mappings[".csv"] = "text/csv; charset=utf-8";
    contentTypeProvider.Mappings[".map"] = "application/json; charset=utf-8";

    app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypeProvider });

    // Одна сводная запись на HTTP-запрос вместо нескольких событий ASP.NET Core.
    // Ставится после статики, чтобы не шуметь на css/js.
    app.UseSerilogRequestLogging(options =>
    {
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath} → {StatusCode} за {Elapsed:0.0} мс";

        options.GetLevel = (http, elapsed, ex) => ex is not null || http.Response.StatusCode >= 500
            ? LogEventLevel.Error
            : http.Response.StatusCode >= 400
                ? LogEventLevel.Warning
                : LogEventLevel.Information;

        options.EnrichDiagnosticContext = (diagnostic, http) =>
        {
            diagnostic.Set("User", http.User.FindFirstValue(ClaimTypes.Name) ?? "anonymous");
            diagnostic.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString() ?? "-");
        };
    });

    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseSession();

    app.MapRazorPages();

    app.MapQueryApi();
    app.MapCompletionApi();
    app.MapAuditApi();
    app.MapDdlApi();
    app.MapDataEditApi();
    app.MapExportApi();
    app.MapImportApi();

    // Метабаза: создать схему/таблицы, перенести ключи Data Protection, завести первого администратора
    await app.Services.InitializePostgresMetaStoreAsync();
    await AdminSeeder.SeedAsync(app.Services);

    // Persistent-снапшоты метаданных: поднять из метабазы в память при старте
    using (var scope = app.Services.CreateScope())
    {
        var persistence = scope.ServiceProvider.GetRequiredService<IMetadataPersistence>();
        await persistence.LoadFromDiskAsync(CancellationToken.None);
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Приложение аварийно завершилось");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}
