using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Net.Http.Headers;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;
using WebDbViewer.Core.Export;

namespace WebDbViewer.Web.Api;

/// <summary>Тело запроса на экспорт результата произвольного SQL в INSERT-скрипт.</summary>
/// <param name="Ds">Идентификатор датасорса.</param>
/// <param name="Sql">Текст запроса, результат которого выгружается.</param>
/// <param name="Target">Имя таблицы-приёмника в скрипте: «таблица» либо «схема.таблица».</param>
/// <param name="Db">База данных сервера, если отличается от базы датасорса.</param>
/// <param name="Limit">Максимум строк; null — без ограничения.</param>
public sealed record ExportQueryRequest(
    Guid Ds,
    string Sql,
    string Target,
    string? Db = null,
    long? Limit = null);

/// <summary>Пределы выгрузки, общие для endpoint'ов и обработчиков страниц.</summary>
public static class ExportLimits
{
    /// <summary>Строк на выгрузку из вкладки редактора: столько же, сколько показывает грид.</summary>
    public const long EditorRowLimit = 5000;
}

/// <summary>
/// Endpoints выгрузки в .sql: структура таблицы (DDL) и/или данные (INSERT-скрипт).
/// ВАЖНО (для владельца Program.cs): перед app.MapExportApi() необходимо зарегистрировать:
///   builder.Services.AddPostgresDdl();   // IDdlGenerator для PostgreSQL
///   builder.Services.AddOracleDdl();     // IDdlGenerator для Oracle
///   builder.Services.AddDbSessions();    // IDbSessionManager, IDataSourceStore, IDbProviderRegistry
/// </summary>
public static class ExportEndpoints
{
    /// <summary>Скрипт пишется без BOM: sqlplus и psql на BOM спотыкаются.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IEndpointRouteBuilder MapExportApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/export/sql", ExportTableAsync);
        api.MapPost("/export/sql/query", ExportQueryAsync);
        return app;
    }

    // ---------------------------------------------------------------- GET /api/export/sql?ds=&schema=&table=&structure=&data=&db=&limit=

    /// <summary>
    /// Выгрузка таблицы в .sql: CREATE TABLE и/или INSERT'ы.
    /// structure и data по умолчанию включены; оба выключены — 400.
    /// limit ограничивает число строк данных, по умолчанию ограничения нет.
    /// Ответ — application/sql как вложение либо JSON с ошибкой.
    /// </summary>
    private static async Task<IResult> ExportTableAsync(
        HttpContext http,
        IDbConnectionFactory connectionFactory,
        IDataSourceStore dataSourceStore,
        IDbProviderRegistry providers,
        IEnumerable<IDdlGenerator> generators,
        Guid ds,
        string? schema,
        string? table,
        bool? structure,
        bool? data,
        string? db,
        long? limit,
        CancellationToken ct)
    {
        if (ds == Guid.Empty || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
            return Results.BadRequest(new { error = "Не заданы параметры ds, schema или table." });

        var withStructure = structure ?? true;
        var withData = data ?? true;
        if (!withStructure && !withData)
            return Results.BadRequest(new { error = "Нечего выгружать: structure и data одновременно выключены." });

        if (limit is <= 0)
            return Results.BadRequest(new { error = "Параметр limit должен быть положительным." });

        var config = await dataSourceStore.GetAsync(ds, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        IDdlGenerator? generator = null;
        if (withStructure)
        {
            generator = generators.FirstOrDefault(g => g.Kind == config.Kind);
            if (generator is null)
                return Results.Problem($"Генератор DDL для «{config.Kind}» не зарегистрирован.", statusCode: 500);
        }

        var provider = providers.Get(config.Kind);
        var target = provider.QuoteIdentifier(schema) + "." + provider.QuoteIdentifier(table);

        try
        {
            // Выгрузка идёт по собственному соединению, а не по сессии пользователя: она может
            // длиться минуты, а сессия — одно соединение, которое всё это время было бы занято.
            // Плата — скрипт не видит незакоммиченных правок открытой транзакции пользователя.
            await using var connection = await connectionFactory.OpenAsync(config, db, ct);

            // DDL читается до первого байта ответа: после начала стрима код ответа уже не сменить.
            string? ddl = null;
            if (withStructure)
            {
                ddl = await DdlText.GetAsync(generator!, connection, schema, table, "table", null, ct);
                if (ddl is null)
                    return Results.BadRequest(new { error = "Генератор не вернул DDL таблицы." });
            }

            await using var command = withData ? connection.CreateCommand() : null;
            if (command is not null)
            {
                command.CommandText = $"SELECT * FROM {target}";
                command.CommandTimeout = config.CommandTimeoutSeconds;
            }

            var header = new[]
            {
                "Выгрузка WebDbViewer",
                $"Датасорс: {config.Name}",
                $"Диалект: {config.Kind}",
                $"Таблица: {schema}.{table}",
                $"Дата (UTC): {UtcNow()}",
                $"Включено: {Included(withStructure, withData)}",
            };

            await WriteScriptAsync(
                http, $"{schema}.{table}.sql", header, ddl, command, target,
                config.Kind, provider.QuoteIdentifier, limit ?? long.MaxValue, ct);

            return Results.Empty;
        }
        catch (DdlObjectNotFoundException ex) when (!http.Response.HasStarted)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (NotSupportedException ex) when (!http.Response.HasStarted)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (DbException ex) when (!http.Response.HasStarted)
        {
            return Results.BadRequest(new { error = $"Ошибка выгрузки: {ex.Message}" });
        }
    }

    // ---------------------------------------------------------------- POST /api/export/sql/query

    /// <summary>
    /// Выгрузка результата произвольного запроса в INSERT-скрипт для таблицы Target.
    /// Структуры в скрипте нет: тип таблицы-приёмника задаёт пользователь, а не источник.
    /// </summary>
    private static async Task<IResult> ExportQueryAsync(
        ExportQueryRequest request,
        HttpContext http,
        IDbConnectionFactory connectionFactory,
        IDataSourceStore dataSourceStore,
        IDbProviderRegistry providers,
        CancellationToken ct)
    {
        if (request.Ds == Guid.Empty || string.IsNullOrWhiteSpace(request.Sql) || string.IsNullOrWhiteSpace(request.Target))
            return Results.BadRequest(new { error = "Не заданы параметры ds, sql или target." });

        if (request.Limit is <= 0)
            return Results.BadRequest(new { error = "Параметр limit должен быть положительным." });

        var config = await dataSourceStore.GetAsync(request.Ds, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var provider = providers.Get(config.Kind);
        var target = QuoteTarget(provider, request.Target.Trim());

        try
        {
            // Своё соединение, как и у выгрузки таблицы: длинный SELECT не должен занимать сессию.
            await using var connection = await connectionFactory.OpenAsync(config, request.Db, ct);

            await using var command = connection.CreateCommand();
            command.CommandText = request.Sql;
            command.CommandTimeout = config.CommandTimeoutSeconds;

            var header = new[]
            {
                "Выгрузка WebDbViewer",
                $"Датасорс: {config.Name}",
                $"Диалект: {config.Kind}",
                $"Таблица-приёмник: {target}",
                $"Дата (UTC): {UtcNow()}",
                "Включено: только данные (результат произвольного запроса)",
            };

            await WriteScriptAsync(
                http, FileName(request.Target), header, ddl: null, command, target,
                config.Kind, provider.QuoteIdentifier, request.Limit ?? long.MaxValue, ct);

            return Results.Empty;
        }
        catch (NotSupportedException ex) when (!http.Response.HasStarted)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (DbException ex) when (!http.Response.HasStarted)
        {
            return Results.BadRequest(new { error = $"Ошибка выгрузки: {ex.Message}" });
        }
    }

    // ---------------------------------------------------------------- Запись скрипта

    /// <summary>
    /// Пишет скрипт прямо в тело ответа. Данные не буферизуются: курсор читается и пишется
    /// построчно, поэтому объём таблицы не превращается в объём памяти процесса.
    /// <paramref name="dataCommand"/> = null — данные не выгружаются.
    /// </summary>
    private static async Task WriteScriptAsync(
        HttpContext http,
        string fileName,
        IReadOnlyList<string> headerLines,
        string? ddl,
        DbCommand? dataCommand,
        string target,
        DbKind kind,
        Func<string, string> quoteIdentifier,
        long maxRows,
        CancellationToken ct)
    {
        // Курсор открывается до записи заголовков: ошибка запроса должна стать кодом ответа,
        // а не комментарием в середине уже отданного файла.
        await using var reader = dataCommand is null ? null : await dataCommand.ExecuteReaderAsync(ct);

        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName); // сам расставит ASCII-filename и filename*=UTF-8''…

        http.Response.ContentType = "application/sql; charset=utf-8";
        http.Response.Headers.ContentDisposition = disposition.ToString();

        await using var writer = new StreamWriter(http.Response.Body, Utf8NoBom, leaveOpen: true);
        await WriteScriptBodyAsync(writer, headerLines, ddl, reader, target, kind, quoteIdentifier, maxRows, ct);
    }

    /// <summary>
    /// Текст скрипта в произвольный <paramref name="writer"/>: шапка-комментарий, DDL, INSERT'ы.
    /// Вынесено из <see cref="WriteScriptAsync"/>, чтобы вкладка редактора собирала ровно тот же
    /// скрипт, что и скачиваемый файл, а не свою похожую версию.
    /// <paramml name="reader"/> = null — данные не выгружаются.
    /// </summary>
    public static async Task WriteScriptBodyAsync(
        TextWriter writer,
        IReadOnlyList<string> headerLines,
        string? ddl,
        DbDataReader? reader,
        string target,
        DbKind kind,
        Func<string, string> quoteIdentifier,
        long maxRows,
        CancellationToken ct)
    {
        foreach (var line in headerLines)
            await writer.WriteLineAsync("-- " + line);
        await writer.WriteLineAsync();

        if (ddl is not null)
        {
            // PostgreSQL отдаёт DDL уже с «;», Oracle DBMS_METADATA — без него.
            // Без этой правки следующий INSERT прилипает к CREATE TABLE.
            var text = ddl.TrimEnd();
            await writer.WriteLineAsync(text.EndsWith(';') ? text : text + ";");
            await writer.WriteLineAsync();
        }

        if (reader is null)
            return;

        var result = await InsertScriptWriter.WriteAsync(reader, target, writer, kind, quoteIdentifier, maxRows, ct);

        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"-- Строк выгружено: {result.RowCount}.");
        if (result.Truncated)
            await writer.WriteLineAsync(
                $"-- ВНИМАНИЕ: вывод оборван на {result.RowCount} строках — в источнике есть ещё данные.");
    }

    /// <summary>Шапка скрипта выгрузки таблицы. Используется и endpoint'ом, и вкладкой редактора.</summary>
    public static string[] TableHeader(DataSourceConfig config, string schema, string table, bool structure, bool data) =>
    [
        "Выгрузка WebDbViewer",
        $"Датасорс: {config.Name}",
        $"Диалект: {config.Kind}",
        $"Таблица: {schema}.{table}",
        $"Дата (UTC): {UtcNow()}",
        $"Включено: {Included(structure, data)}",
    ];

    // ---------------------------------------------------------------- Мелочи

    private static string UtcNow() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Included(bool structure, bool data) => (structure, data) switch
    {
        (true, true) => "структура и данные",
        (true, false) => "только структура",
        _ => "только данные",
    };

    /// <summary>
    /// Квотирует имя таблицы-приёмника, пришедшее от пользователя.
    /// ponytail: разделитель — первая точка. Имя схемы или таблицы с точкой внутри
    /// так не разобрать; появится потребность — принимать схему отдельным полем запроса.
    /// </summary>
    private static string QuoteTarget(IDbProvider provider, string target)
    {
        var dot = target.IndexOf('.');
        return dot > 0 && dot < target.Length - 1
            ? provider.QuoteIdentifier(target[..dot]) + "." + provider.QuoteIdentifier(target[(dot + 1)..])
            : provider.QuoteIdentifier(target);
    }

    /// <summary>Имя файла выгрузки: имя приёмника без кавычек плюс расширение.</summary>
    private static string FileName(string target) => target.Trim().Replace("\"", "") + ".sql";
}
