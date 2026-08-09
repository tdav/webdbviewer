using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using WebDbViewer.Core;
using WebDbViewer.Core.Sessions;

namespace WebDbViewer.Web.Api;

/// <summary>Тело запроса на выполнение SQL.</summary>
/// <param name="DsId">Идентификатор датасорса.</param>
/// <param name="Sql">Текст скрипта.</param>
/// <param name="SessionId">Существующая сессия БД (если нет — создаётся/переиспользуется по пользователю).</param>
/// <param name="CaretOffset">Позиция каретки: выполняется statement под курсором.</param>
/// <param name="WholeScript">true — выполнить весь скрипт, игнорируя позицию каретки.</param>
/// <param name="Db">База данных сервера, если отличается от базы датасорса (навигатор по всем базам).</param>
public sealed record ExecuteQueryRequest(
    Guid DsId,
    string Sql,
    Guid? SessionId = null,
    int? CaretOffset = null,
    bool WholeScript = false,
    string? Db = null);

/// <summary>
/// Endpoints выполнения SQL и постраничного чтения данных таблиц.
/// ВАЖНО (для владельца Program.cs): перед app.MapQueryApi() необходимо зарегистрировать сервисы:
///   builder.Services.AddResultStreaming();   // ExecutionRegistry (этот модуль)
///   builder.Services.AddDbSessions();        // IDbSessionManager, IDataSourceStore, IDbProviderRegistry
/// IStatementSplitter и IQueryAuditor опциональны: без них используется наивный сплит и аудит не пишется.
/// </summary>
public static class QueryEndpoints
{
    /// <summary>Размер батча строк в SSE-событии row.</summary>
    public const int RowBatchSize = 100;

    /// <summary>Лимит строк на выполнение по умолчанию.</summary>
    public const int DefaultMaxRows = 10_000;

    public static IEndpointRouteBuilder MapQueryApi(this IEndpointRouteBuilder app)
    {
        // Все endpoints требуют авторизации.
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/query/execute", ExecuteAsync);
        api.MapGet("/query/stream/{executionId:guid}", StreamAsync);
        api.MapPost("/query/cancel/{executionId:guid}", Cancel);
        api.MapGet("/data/page", GetDataPageAsync);
        return app;
    }

    // ---------------------------------------------------------------- POST /api/query/execute

    private static async Task<IResult> ExecuteAsync(
        ExecuteQueryRequest request,
        HttpContext http,
        IDbSessionManager sessionManager,
        IDataSourceStore dataSourceStore,
        ExecutionRegistry registry,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (request.DsId == Guid.Empty || string.IsNullOrWhiteSpace(request.Sql))
            return Results.BadRequest(new { error = "Не задан датасорс или текст запроса." });

        var config = await dataSourceStore.GetAsync(request.DsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var userName = http.User.Identity?.Name ?? "anonymous";

        // Берём существующую сессию или создаём/переиспользуем по (пользователь, датасорс).
        IDbSession? session = null;
        if (request.SessionId is { } sid)
            session = await sessionManager.FindAsync(sid, ct);
        session ??= await sessionManager.GetOrCreateAsync(userName, request.DsId, request.Db, ct);

        // Сплит скрипта: полноценный IStatementSplitter из DI, иначе наивный фолбэк.
        var splitter = services.GetService<IStatementSplitter>() ?? NaiveStatementSplitter.Instance;
        IReadOnlyList<SqlStatement> statements;
        try
        {
            statements = splitter.Split(request.Sql, config.Kind);
        }
        catch
        {
            // Сплиттер не должен ронять выполнение — откатываемся на наивный.
            statements = NaiveStatementSplitter.Instance.Split(request.Sql, config.Kind);
        }
        if (statements.Count == 0)
            return Results.BadRequest(new { error = "Скрипт не содержит SQL-операторов." });

        // Statement под курсором или весь скрипт.
        if (!request.WholeScript && request.CaretOffset is { } caret)
        {
            var current = FindStatementAtCaret(statements, caret);
            statements = [current];
        }

        var running = new RunningQuery
        {
            DataSourceId = request.DsId,
            UserName = userName,
            SqlText = string.Join(";\n", statements.Select(s => s.Text)),
            Statements = statements,
            Session = session,
            MaxRows = DefaultMaxRows,
            CommandTimeoutSeconds = config.CommandTimeoutSeconds,
            ClientIp = http.Connection.RemoteIpAddress?.ToString(),
        };
        registry.Register(running);

        return Results.Ok(new
        {
            executionId = running.ExecutionId,
            sessionId = session.SessionId,
            statementCount = statements.Count,
        });
    }

    /// <summary>Statement, в границах которого стоит каретка; иначе ближайший перед ней; иначе первый.</summary>
    public static SqlStatement FindStatementAtCaret(IReadOnlyList<SqlStatement> statements, int caret)
    {
        SqlStatement? before = null;
        foreach (var st in statements)
        {
            if (caret >= st.Offset && caret <= st.Offset + st.Length)
                return st;
            if (st.Offset <= caret)
                before = st;
        }
        return before ?? statements[0];
    }

    // ---------------------------------------------------------------- GET /api/query/stream/{executionId}

    private static async Task StreamAsync(
        Guid executionId,
        HttpContext http,
        ExecutionRegistry registry,
        IServiceProvider services)
    {
        var response = http.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // отключить буферизацию reverse-proxy

        if (!registry.TryGet(executionId, out var running) || !running.TryBeginStreaming())
        {
            await WriteEventAsync(response, "error",
                new { message = "Выполнение не найдено или стрим уже подключён." }, http.RequestAborted);
            return;
        }

        // Аудит опционален: сервис может отсутствовать в DI — не падаем.
        var auditor = services.GetService<IQueryAuditor>();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, running.Cancellation);
        var ct = linked.Token;

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        long totalRows = 0;
        long? affectedRows = null;
        var truncated = false;
        string? error = null;

        try
        {
            foreach (var statement in running.Statements)
            {
                ct.ThrowIfCancellationRequested();

                await using var cmd = running.Session.Connection.CreateCommand();
                cmd.CommandText = statement.Text;
                cmd.CommandTimeout = running.CommandTimeoutSeconds;

                // Регистрируем команду в сессии для CancelRunning() (конкретная реализация DbSession).
                using var scope = (running.Session as DbSession)?.TrackCommand(cmd);

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                if (reader.FieldCount > 0)
                {
                    // meta: описание колонок resultset.
                    var columns = Enumerable.Range(0, reader.FieldCount)
                        .Select(i => new { name = reader.GetName(i), dataType = reader.GetDataTypeName(i) })
                        .ToArray();
                    await WriteEventAsync(response, "meta", new { columns }, ct);

                    // row: батчи по RowBatchSize строк.
                    var batch = new List<object?[]>(RowBatchSize);
                    while (totalRows < running.MaxRows && await reader.ReadAsync(ct))
                    {
                        var row = new object?[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                            row[i] = FormatValue(reader.GetValue(i));
                        batch.Add(row);
                        totalRows++;

                        if (batch.Count >= RowBatchSize)
                        {
                            await WriteEventAsync(response, "row", new { rows = batch }, ct);
                            batch.Clear();
                        }
                    }
                    if (batch.Count > 0)
                        await WriteEventAsync(response, "row", new { rows = batch }, ct);

                    // Достигнут лимит и есть ещё строки — резалт обрезан.
                    if (totalRows >= running.MaxRows && await reader.ReadAsync(ct))
                    {
                        truncated = true;
                        break;
                    }
                }
                else if (reader.RecordsAffected >= 0)
                {
                    affectedRows = (affectedRows ?? 0) + reader.RecordsAffected;
                }
            }
        }
        catch (OperationCanceledException)
        {
            error = "Запрос отменён.";
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            sw.Stop();
            running.MarkFinished();
            registry.TryRemove(executionId);
        }

        // done/error пишем best-effort: клиент мог уже отключиться.
        try
        {
            if (error is null)
                await WriteEventAsync(response, "done", new
                {
                    rowCount = totalRows,
                    affectedRows,
                    elapsedMs = sw.ElapsedMilliseconds,
                    truncated,
                }, CancellationToken.None);
            else
                await WriteEventAsync(response, "error", new { message = error }, CancellationToken.None);
        }
        catch
        {
            // соединение закрыто клиентом
        }

        // Запись аудита (обязателен для гос-сектора; сервис может отсутствовать).
        if (auditor is not null)
        {
            try
            {
                await auditor.RecordAsync(new AuditEntry
                {
                    UserName = running.UserName,
                    DataSourceId = running.DataSourceId,
                    SqlText = running.SqlText,
                    StartedAt = startedAt,
                    Duration = sw.Elapsed,
                    RowsAffected = affectedRows ?? (totalRows > 0 ? totalRows : null),
                    Success = error is null,
                    ErrorMessage = error,
                    ClientIp = running.ClientIp,
                }, CancellationToken.None);
            }
            catch
            {
                // Сбой аудита не должен ломать ответ клиенту.
            }
        }
    }

    private static async Task WriteEventAsync(HttpResponse response, string eventName, object payload, CancellationToken ct)
    {
        await response.WriteAsync(SseFormatter.FormatJson(eventName, payload), ct);
        await response.Body.FlushAsync(ct);
    }

    // ---------------------------------------------------------------- POST /api/query/cancel/{executionId}

    private static IResult Cancel(Guid executionId, ExecutionRegistry registry)
    {
        if (!registry.TryGet(executionId, out var running))
            return Results.NotFound(new { error = "Выполнение не найдено или уже завершено." });

        running.Cancel(); // CancellationTokenSource + IDbSession.CancelRunning()
        return Results.Ok(new { cancelled = true });
    }

    // ---------------------------------------------------------------- GET /api/data/page

    private static async Task<IResult> GetDataPageAsync(
        HttpContext http,
        IDbSessionManager sessionManager,
        IDataSourceStore dataSourceStore,
        IDbProviderRegistry providers,
        Guid dsId,
        string schema,
        string table,
        int limit = 200,
        string? after = null,
        string? orderBy = null,
        bool desc = false,
        string? filter = null,
        string? db = null,
        CancellationToken ct = default)
    {
        if (dsId == Guid.Empty || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
            return Results.BadRequest(new { error = "Не заданы датасорс, схема или таблица." });

        var config = await dataSourceStore.GetAsync(dsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var userName = http.User.Identity?.Name ?? "anonymous";
        var session = await sessionManager.GetOrCreateAsync(userName, dsId, db, ct);
        var provider = providers.Get(config.Kind);

        var tableInfo = await provider.GetTableInfoAsync(session.Connection, schema, table, ct);

        // Значения keyset-ключа последней строки предыдущей страницы (JSON-массив).
        IReadOnlyList<object?>? afterValues = null;
        if (!string.IsNullOrWhiteSpace(after))
        {
            try
            {
                afterValues = JsonSerializer.Deserialize<List<JsonElement>>(after)?
                    .Select(FromJsonElement).ToList();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Некорректный параметр after (ожидается JSON-массив)." });
            }
        }

        var request = new DataPageRequest
        {
            Schema = schema,
            Table = table,
            Limit = Math.Clamp(limit, 1, 1000),
            After = afterValues,
            OrderBy = string.IsNullOrWhiteSpace(orderBy) ? null : orderBy,
            OrderDescending = desc,
            WhereFilter = string.IsNullOrWhiteSpace(filter) ? null : filter,
        };

        var sql = provider.BuildSelectPageSql(tableInfo, request);

        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = config.CommandTimeoutSeconds;
        if (afterValues is { Count: > 0 })
        {
            // Параметры @after{i} (PG) / :after{i} (Oracle) — имя без префикса подходит обоим провайдерам.
            for (var i = 0; i < afterValues.Count; i++)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"after{i}";
                p.Value = afterValues[i] ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }

        using var scope = (session as DbSession)?.TrackCommand(cmd);

        var columns = new List<ColumnDesc>();
        var rows = new List<object?[]>();
        object?[]? lastRow = null;
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new ColumnDesc(reader.GetName(i), reader.GetDataTypeName(i)));

            while (rows.Count < request.Limit && await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    row[i] = FormatValue(reader.GetValue(i));
                rows.Add(row);
                lastRow = row;
            }
        }
        catch (DbException ex)
        {
            return Results.BadRequest(new { error = $"Ошибка выполнения запроса: {ex.Message}" });
        }

        // nextAfter: значения keyset-ключа последней строки — только если страница заполнена целиком.
        object?[]? nextAfter = null;
        if (lastRow is not null && rows.Count >= request.Limit)
        {
            var keyColumns = ComputeKeyColumns(tableInfo, request, provider.RowAddressPseudoColumn);
            nextAfter = keyColumns
                .Select(k =>
                {
                    var idx = columns.FindIndex(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase));
                    return idx >= 0 ? lastRow[idx] : null;
                })
                .ToArray();
        }

        return Results.Json(new
        {
            columns = columns.Select(c => new { name = c.Name, dataType = c.DataType }),
            rows,
            nextAfter,
        }, SseFormatter.JsonOptions);
    }

    private sealed record ColumnDesc(string Name, string DataType);

    /// <summary>
    /// Колонки keyset-ключа — зеркалит логику провайдеров в BuildSelectPageSql:
    /// явный OrderBy (+PK как tiebreaker), иначе PK, иначе псевдоколонка адреса строки (ctid/ROWID).
    /// </summary>
    public static IReadOnlyList<string> ComputeKeyColumns(TableInfo table, DataPageRequest request, string? rowAddressColumn)
    {
        var hasPk = table.PrimaryKeyColumns.Count > 0;
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.OrderBy))
        {
            keys.Add(request.OrderBy!);
            keys.AddRange(table.PrimaryKeyColumns.Where(c => !string.Equals(c, request.OrderBy, StringComparison.Ordinal)));
            if (!hasPk && rowAddressColumn is not null)
                keys.Add(rowAddressColumn);
        }
        else if (hasPk)
        {
            keys.AddRange(table.PrimaryKeyColumns);
        }
        else if (rowAddressColumn is not null)
        {
            keys.Add(rowAddressColumn);
        }
        return keys;
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>Приводит значение из DbDataReader к JSON-сериализуемому виду (null = NULL).</summary>
    public static object? FormatValue(object? value) => value switch
    {
        null or DBNull => null,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or string => value,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
        TimeSpan ts => ts.ToString(),
        Guid g => g.ToString(),
        char c => c.ToString(),
        byte[] bytes => FormatBytes(bytes),
        _ => value.ToString(), // прочие провайдер-специфичные типы — строкой
    };

    private static string FormatBytes(byte[] bytes)
    {
        const int maxShown = 64;
        var hex = Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, maxShown));
        return bytes.Length > maxShown ? $"0x{hex}… ({bytes.Length} байт)" : $"0x{hex}";
    }

    /// <summary>Конвертация JsonElement (параметр after) в примитивное значение для DbParameter.</summary>
    public static object? FromJsonElement(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        // (object)l — иначе тернарный оператор приводит long к decimal.
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDecimal(),
        JsonValueKind.String => el.GetString(),
        _ => el.GetRawText(),
    };
}
