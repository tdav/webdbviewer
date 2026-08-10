using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>
/// Endpoint импорта .sql-файла: приём multipart/form-data и выполнение скрипта на подключении.
/// ВАЖНО (для владельца Program.cs): достаточно вызвать app.MapImportApi() рядом с app.MapQueryApi();
/// дополнительных регистраций сервисов не требуется — используются уже имеющиеся
/// IDbConnectionFactory, IDataSourceStore и IAntiforgery (AddAntiforgery уже настроен).
/// IStatementSplitter и IQueryAuditor опциональны: без них берётся наивный сплит и аудит не пишется.
/// </summary>
public static class ImportEndpoints
{
    /// <summary>Предельный размер загружаемого файла.</summary>
    public const int MaxUploadBytes = 32 * 1024 * 1024;

    /// <summary>Сколько символов упавшего statement возвращается клиенту.</summary>
    private const int SqlPreviewLength = 200;

    /// <summary>Режим «всё в одной транзакции» (по умолчанию).</summary>
    private const string ModeTransaction = "transaction";

    /// <summary>Режим «продолжать после ошибки»: statements идут по одному, вне общей транзакции.</summary>
    private const string ModeContinue = "continue";

    public static IEndpointRouteBuilder MapImportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/import/sql", ImportSqlAsync);
        return app;
    }

    // ---------------------------------------------------------------- POST /api/import/sql

    /// <summary>
    /// Импорт SQL-скрипта. multipart/form-data: файл в поле «file», плюс поля ds (Guid),
    /// db (опционально) и mode (transaction | continue).
    /// </summary>
    /// <remarks>
    /// Про antiforgery. В Program.cs настроен <c>options.HeaderName = "RequestVerificationToken"</c>,
    /// и _Layout отдаёт токен через hx-headers — то есть защита работает и её отключать нельзя.
    /// Но <c>app.UseAntiforgery()</c> в конвейере Program.cs НЕ вызывается, а minimal API навешивает
    /// требование antiforgery-middleware на любой endpoint, который связывает форму параметрами
    /// (IFormFile / IFormCollection / [FromForm]): такой маршрут падал бы на каждом запросе, и
    /// единственным выходом стал бы <c>.DisableAntiforgery()</c>, то есть отказ от защиты.
    /// Поэтому форма читается вручную из HttpContext (автоматическая antiforgery-метадата не
    /// добавляется, middleware не требуется), а токен проверяется явным вызовом
    /// <see cref="IAntiforgery.ValidateRequestAsync"/> — он самодостаточен и читает токен именно
    /// из настроенного заголовка. Ручное чтение формы нужно и по второй причине: предел размера тела
    /// у Kestrel по умолчанию ~30 МБ, то есть меньше <see cref="MaxUploadBytes"/>, а поднять его
    /// через IHttpMaxRequestBodySizeFeature можно только до первого чтения тела.
    /// </remarks>
    private static async Task<IResult> ImportSqlAsync(
        HttpContext http,
        IAntiforgery antiforgery,
        IDbConnectionFactory connectionFactory,
        IDataSourceStore dataSourceStore,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
            return Error(StatusCodes.Status400BadRequest, "Ожидается multipart/form-data с файлом в поле «file».");

        // Поднимаем предел тела запроса до нашего лимита — строго до первого чтения тела.
        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = MaxUploadBytes;

        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return Error(StatusCodes.Status400BadRequest, "Недействительный или отсутствующий antiforgery-токен.");
        }

        IFormCollection form;
        try
        {
            form = await http.Request.ReadFormAsync(ct);
        }
        catch (BadHttpRequestException)
        {
            // Kestrel обрывает чтение, когда тело превысило поднятый выше предел.
            return Error(StatusCodes.Status413PayloadTooLarge,
                $"Файл слишком велик: допускается не более {MaxUploadBytes / (1024 * 1024)} МБ.");
        }

        // ---------- Разбор полей формы ----------

        if (!Guid.TryParse(form["ds"].ToString(), out var dsId) || dsId == Guid.Empty)
            return Error(StatusCodes.Status400BadRequest, "Не задан или некорректен параметр ds.");

        var db = form["db"].ToString();
        if (string.IsNullOrWhiteSpace(db))
            db = null;

        var mode = form["mode"].ToString();
        if (string.IsNullOrWhiteSpace(mode))
            mode = ModeTransaction;
        if (mode is not (ModeTransaction or ModeContinue))
            return Error(StatusCodes.Status400BadRequest, $"Неизвестный режим импорта: «{mode}». Ожидается transaction или continue.");
        var transactional = mode == ModeTransaction;

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Error(StatusCodes.Status400BadRequest, "Файл не передан или пуст.");
        if (file.Length > MaxUploadBytes)
            return Error(StatusCodes.Status413PayloadTooLarge,
                $"Файл слишком велик: {file.Length / (1024 * 1024)} МБ при допустимых {MaxUploadBytes / (1024 * 1024)} МБ.");

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "script.sql";

        // ---------- Датасорс и запрет записи ----------

        var config = await dataSourceStore.GetAsync(dsId, ct);
        if (config is null)
            return Error(StatusCodes.Status404NotFound, "Датасорс не найден.");

        // Read-only датасорс: отказ до чтения файла и до любого выполнения.
        if (config.ReadOnly)
            return Error(StatusCodes.Status403Forbidden,
                "Датасорс открыт только для чтения: импорт SQL-скрипта запрещён.");

        // ---------- Содержимое скрипта ----------

        string script;
        // detectEncodingFromByteOrderMarks: BOM (в том числе UTF-8) съедается, без BOM читаем как UTF-8.
        using (var reader = new StreamReader(
                   file.OpenReadStream(),
                   new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   detectEncodingFromByteOrderMarks: true))
        {
            script = await reader.ReadToEndAsync(ct);
        }

        // Сплит скрипта: полноценный IStatementSplitter из DI, иначе наивный фолбэк.
        var splitter = services.GetService<IStatementSplitter>() ?? NaiveStatementSplitter.Instance;
        IReadOnlyList<SqlStatement> statements;
        try
        {
            statements = splitter.Split(script, config.Kind);
        }
        catch
        {
            // Сплиттер не должен ронять импорт — откатываемся на наивный.
            statements = NaiveStatementSplitter.Instance.Split(script, config.Kind);
        }
        if (statements.Count == 0)
            return Error(StatusCodes.Status400BadRequest, "Файл не содержит SQL-операторов.");

        // ---------- Выполнение ----------

        var userName = http.User.Identity?.Name ?? "anonymous";

        // Импорт идёт по собственному соединению, а не по сессии пользователя: скрипт может
        // выполняться минуты, и всё это время сессия (одно соединение) была бы занята.
        // Побочная выгода — импорт не втягивается в открытую транзакцию пользователя
        // и не может её откатить, поэтому отдельная проверка на чужую транзакцию не нужна.
        await using var connection = await connectionFactory.OpenAsync(config, db, ct);

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var errors = new List<ImportError>();
        var executed = 0;
        long rowsAffected = 0;
        var cancelled = false;

        var transaction = transactional ? await connection.BeginTransactionAsync(ct) : null;
        await using var transactionScope = transaction;

        try
        {
            for (var i = 0; i < statements.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = statements[i].Text;
                    cmd.CommandTimeout = config.CommandTimeoutSeconds;
                    cmd.Transaction = transaction;

                    var affected = await cmd.ExecuteNonQueryAsync(ct);
                    if (affected > 0)
                        rowsAffected += affected;
                    executed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Номер statement — с единицы: он показывается пользователю.
                    errors.Add(new ImportError(i + 1, ex.Message, Preview(statements[i].Text)));
                    if (transactional)
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            errors.Add(new ImportError(executed + 1, "Импорт отменён.", string.Empty));
        }

        if (transaction is not null)
        {
            if (errors.Count == 0)
            {
                await transaction.CommitAsync(ct);
            }
            else
            {
                // Токен уже мог быть отменён — откат выполняем безусловно.
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    errors.Add(new ImportError(0, $"Не удалось откатить транзакцию: {ex.Message}", string.Empty));
                }

                // После отката ни один statement не применён — отчёт должен это отражать.
                executed = 0;
                rowsAffected = 0;
            }
        }

        sw.Stop();

        var success = errors.Count == 0;
        var errorMessage = success
            ? null
            : cancelled
                ? "Импорт отменён."
                : $"Ошибок: {errors.Count}; первая — statement №{errors[0].Index}: {errors[0].Message}";

        // ---------- Аудит: одна запись на импорт ----------

        // Весь файл в аудит не пишем — он может быть огромным; хватает сводки.
        var auditor = services.GetService<IQueryAuditor>();
        if (auditor is not null)
        {
            try
            {
                await auditor.RecordAsync(new AuditEntry
                {
                    UserName = userName,
                    DataSourceId = dsId,
                    SqlText = $"-- Импорт SQL-скрипта «{fileName}»: {statements.Count} statements, режим {mode}",
                    StartedAt = startedAt,
                    Duration = sw.Elapsed,
                    RowsAffected = rowsAffected,
                    Success = success,
                    ErrorMessage = errorMessage,
                    ClientIp = http.Connection.RemoteIpAddress?.ToString(),
                }, CancellationToken.None);
            }
            catch
            {
                // Сбой аудита не должен ломать ответ клиенту.
            }
        }

        return Results.Json(new
        {
            statements = statements.Count,
            executed,
            rowsAffected,
            elapsedMs = sw.ElapsedMilliseconds,
            mode,
            errors,
        }, SseFormatter.JsonOptions);
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>Ошибка на statement: номер с единицы, текст СУБД и начало упавшего SQL.</summary>
    private sealed record ImportError(int Index, string Message, string Sql);

    /// <summary>Ответ об ошибке с русским текстом (кириллица не экранируется).</summary>
    private static IResult Error(int statusCode, string message) =>
        Results.Json(new { error = message }, SseFormatter.JsonOptions, statusCode: statusCode);

    /// <summary>Начало statement для показа в ошибке.</summary>
    private static string Preview(string sql) =>
        sql.Length <= SqlPreviewLength ? sql : sql[..SqlPreviewLength] + "…";
}
