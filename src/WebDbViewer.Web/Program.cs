using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using WebDbViewer.Completion;
using WebDbViewer.Core.Sessions;
using WebDbViewer.Metadata;
using WebDbViewer.Providers.Oracle;
using WebDbViewer.Providers.Postgres;
using WebDbViewer.Web;
using WebDbViewer.Web.Api;
using WebDbViewer.Web.Audit;

var builder = WebApplication.CreateBuilder(args);

// ---------- Razor Pages: авторизация по умолчанию для всех страниц, логин — анонимный ----------
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Login");
});

// ---------- Data Protection: ключи сохраняются в ./keys (переживают рестарт) ----------
builder.Services.AddDataProtection()
    .SetApplicationName("WebDbViewer")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys")));

// ---------- Cookie-аутентификация (один пользователь из конфигурации, пароль — хэш PBKDF2) ----------
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
builder.Services.AddDbSessions(options =>
{
    var configured = builder.Configuration["DataSources:FilePath"];
    if (!string.IsNullOrWhiteSpace(configured))
        options.DataSourcesFilePath = configured;
});
builder.Services.AddMetadataCache(Path.Combine(builder.Environment.ContentRootPath, "metadata.db"));

// SQL-интеллект: IStatementSplitter, ICompletionEngine, ISqlDiagnosticsService
builder.Services.AddSqlIntelliSense();
// Аудит запросов: IQueryAuditor поверх отдельного audit.db
builder.Services.AddQueryAudit(options =>
{
    var configured = builder.Configuration["Audit:DbPath"];
    options.DbPath = !string.IsNullOrWhiteSpace(configured)
        ? configured
        : Path.Combine(builder.Environment.ContentRootPath, "audit.db");
});
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

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();

app.MapRazorPages();

app.MapQueryApi();
app.MapCompletionApi();
app.MapAuditApi();

// Persistent-снапшоты метаданных: поднять с диска в память при старте
using (var scope = app.Services.CreateScope())
{
    var persistence = scope.ServiceProvider.GetRequiredService<IMetadataPersistence>();
    await persistence.LoadFromDiskAsync(CancellationToken.None);
}

app.Run();
