using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using WebDbViewer.Core;
using WebDbViewer.Core.Editing;
using WebDbViewer.Core.Sessions;

namespace WebDbViewer.Web.Api;

/// <summary>Одно изменение строки в запросе редактирования (kind: insert | update | delete).</summary>
public sealed record RowEditDto(
    string Schema,
    string Table,
    string Kind,
    Dictionary<string, JsonElement>? KeyValues,
    Dictionary<string, JsonElement>? ChangedValues);

/// <summary>Тело запроса пакетного редактирования данных. Db — база сервера, если отличается от базы датасорса.</summary>
public sealed record DataEditRequest(Guid DsId, Guid? SessionId, List<RowEditDto> Edits, string? Db = null);

/// <summary>
/// Endpoints inline-редактирования данных таблиц.
/// ВАЖНО (для владельца Program.cs): перед app.MapDataEditApi() необходимо зарегистрировать:
///   builder.Services.AddDbSessions();              // IDbSessionManager, IDataSourceStore, IDbProviderRegistry
///   builder.Services.AddPostgresDmlGeneration();   // IDmlGenerator (PG)
///   builder.Services.AddOracleDmlGeneration();     // IDmlGenerator (Oracle)
/// IQueryAuditor опционален: без него аудит не пишется.
/// </summary>
public static class DataEditEndpoints
{
    public static IEndpointRouteBuilder MapDataEditApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/data/edit", EditAsync);
        api.MapGet("/data/edit/table-info", GetTableInfoAsync);
        return app;
    }

    // ---------------------------------------------------------------- GET /api/data/edit/table-info

    /// <summary>Метаданные таблицы для клиентского редактора: PK, псевдоколонка адреса строки, колонки.</summary>
    private static async Task<IResult> GetTableInfoAsync(
        HttpContext http,
        IDbSessionManager sessionManager,
        IDataSourceStore dataSourceStore,
        IDbProviderRegistry providers,
        Guid dsId,
        string schema,
        string table,
        string? db,
        CancellationToken ct)
    {
        if (dsId == Guid.Empty || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
            return Results.BadRequest(new { error = "Не заданы датасорс, схема или таблица." });

        var config = await dataSourceStore.GetAsync(dsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var userName = http.User.Identity?.Name ?? "anonymous";
        var session = await sessionManager.GetOrCreateAsync(userName, dsId, db, ct);
        var provider = providers.Get(config.Kind);

        TableInfo info;
        try
        {
            info = await provider.GetTableInfoAsync(session.Connection, schema, table, ct);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // Имя псевдоколонки адреса строки, как она приходит в SELECT-странице данных:
        // PG — ctid; Oracle — алиас __ROWID (ROWID AS "__ROWID").
        var rowAddress = provider.RowAddressPseudoColumn is null
            ? null
            : config.Kind == DbKind.Oracle ? "__ROWID" : provider.RowAddressPseudoColumn;

        return Results.Json(new
        {
            readOnly = config.ReadOnly,
            primaryKey = info.PrimaryKeyColumns,
            rowAddressColumn = info.PrimaryKeyColumns.Count > 0 ? null : rowAddress,
            columns = info.Columns.Select(c => new
            {
                name = c.Name,
                dataType = c.DataType,
                isNullable = c.IsNullable,
                isPrimaryKey = c.IsPrimaryKey,
                isGenerated = c.IsGenerated,
                defaultValue = c.DefaultValue,
            }),
        }, SseFormatter.JsonOptions);
    }

    // ---------------------------------------------------------------- POST /api/data/edit

    private static async Task<IResult> EditAsync(
        DataEditRequest request,
        HttpContext http,
        IDbSessionManager sessionManager,
        IDataSourceStore dataSourceStore,
        IDbProviderRegistry providers,
        IEnumerable<IDmlGenerator> generators,
        IServiceProvider services,
        CancellationToken ct)
    {
        if (request.DsId == Guid.Empty || request.Edits is not { Count: > 0 })
            return Results.BadRequest(new { error = "Не задан датасорс или список изменений пуст." });

        var config = await dataSourceStore.GetAsync(request.DsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        // Read-only датасорс: редактирование запрещено (дополнительно PG-сессия открыта в read-only режиме).
        if (config.ReadOnly)
            return Results.Json(
                new { error = "Датасорс доступен только для чтения — редактирование данных запрещено." },
                SseFormatter.JsonOptions, statusCode: StatusCodes.Status403Forbidden);

        var generator = generators.FirstOrDefault(g => g.Kind == config.Kind);
        if (generator is null)
            return Results.Json(
                new { error = $"Генератор DML для СУБД {config.Kind} не зарегистрирован." },
                SseFormatter.JsonOptions, statusCode: StatusCodes.Status500InternalServerError);

        var userName = http.User.Identity?.Name ?? "anonymous";
        IDbSession? session = null;
        if (request.SessionId is { } sid)
            session = await sessionManager.FindAsync(sid, ct);
        session ??= await sessionManager.GetOrCreateAsync(userName, request.DsId, request.Db, ct);

        var provider = providers.Get(config.Kind);

        // Транзакционность: auto-commit — весь пакет атомарно (commit в конце / rollback при ошибке);
        // manual-commit — работаем в открытой транзакции пользователя (не начата — начинаем), commit делает он сам.
        var manualCommit = !session.AutoCommit;
        var startedTransaction = false;
        if (!session.InTransaction)
        {
            await session.BeginTransactionAsync(ct);
            startedTransaction = true;
        }

        var results = new List<object>(request.Edits.Count);
        var executedSql = new List<string>();
        var applied = 0;
        var failed = 0;
        string? fatalError = null;
        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        long totalAffected = 0;

        for (var i = 0; i < request.Edits.Count; i++)
        {
            var dto = request.Edits[i];
            DmlCommand dml;
            try
            {
                var edit = ToRowEdit(dto);
                var tableInfo = await provider.GetTableInfoAsync(session.Connection, dto.Schema, dto.Table, ct);
                dml = generator.Build(tableInfo, edit);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Ошибка построения DML (плохие входные данные) — SQL не выполнялся, продолжаем пакет.
                failed++;
                results.Add(new { index = i, success = false, error = ex.Message });
                continue;
            }

            try
            {
                await using var cmd = dml.CreateDbCommand(session.Connection);
                cmd.CommandTimeout = config.CommandTimeoutSeconds;
                using var scope = (session as DbSession)?.TrackCommand(cmd); // привязка к транзакции сессии
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                executedSql.Add(dml.Sql);
                applied++;
                totalAffected += Math.Max(affected, 0);
                results.Add(new { index = i, success = true, affectedRows = affected });
            }
            catch (Exception ex) when (ex is DbException or OperationCanceledException)
            {
                // Ошибка выполнения: транзакция в PostgreSQL становится abort — пакет прерываем.
                failed++;
                executedSql.Add(dml.Sql);
                var message = ex is OperationCanceledException ? "Операция отменена." : ex.Message;
                results.Add(new { index = i, success = false, error = message });
                fatalError = message;
                break;
            }
        }

        // Строки после точки прерывания не выполнялись.
        for (var i = results.Count; i < request.Edits.Count; i++)
            results.Add(new { index = i, success = false, error = "Не выполнено: пакет прерван предыдущей ошибкой." });

        var committed = false;
        var rolledBack = false;
        if (startedTransaction && !manualCommit)
        {
            // Auto-commit: пакет атомарен — фиксируем только при полном успехе.
            try
            {
                if (fatalError is null && failed == 0)
                {
                    await session.CommitAsync(CancellationToken.None);
                    committed = true;
                }
                else
                {
                    await session.RollbackAsync(CancellationToken.None);
                    rolledBack = true;
                    applied = 0; // всё откатано
                }
            }
            catch (Exception ex)
            {
                fatalError ??= $"Ошибка завершения транзакции: {ex.Message}";
            }
        }
        // Manual-commit: транзакция остаётся открытой — фиксацию/откат выполняет пользователь.

        sw.Stop();

        // Аудит (best-effort, сервис может отсутствовать).
        var auditor = services.GetService<IQueryAuditor>();
        if (auditor is not null && executedSql.Count > 0)
        {
            try
            {
                await auditor.RecordAsync(new AuditEntry
                {
                    UserName = userName,
                    DataSourceId = request.DsId,
                    SqlText = string.Join(";\n", executedSql),
                    StartedAt = startedAt,
                    Duration = sw.Elapsed,
                    RowsAffected = totalAffected,
                    Success = fatalError is null && failed == 0,
                    ErrorMessage = fatalError,
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
            applied,
            failed = request.Edits.Count - applied,
            committed,
            rolledBack,
            inTransaction = session.InTransaction,
            sessionId = session.SessionId,
            results,
        }, SseFormatter.JsonOptions);
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>Преобразует DTO в доменную модель RowEdit (kind-строка → enum, JsonElement → CLR-значения).</summary>
    public static RowEdit ToRowEdit(RowEditDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Schema) || string.IsNullOrWhiteSpace(dto.Table))
            throw new InvalidOperationException("Не заданы схема или таблица изменяемой строки.");

        var kind = dto.Kind?.ToLowerInvariant() switch
        {
            "insert" => RowEditKind.Insert,
            "update" => RowEditKind.Update,
            "delete" => RowEditKind.Delete,
            _ => throw new InvalidOperationException($"Неизвестный вид операции: «{dto.Kind}» (ожидается insert, update или delete)."),
        };

        return new RowEdit
        {
            Schema = dto.Schema,
            Table = dto.Table,
            Kind = kind,
            KeyValues = Convert(dto.KeyValues),
            ChangedValues = Convert(dto.ChangedValues),
        };
    }

    private static IReadOnlyDictionary<string, object?> Convert(Dictionary<string, JsonElement>? source)
    {
        if (source is null || source.Count == 0)
            return new Dictionary<string, object?>();
        // Порядок ключей сохраняется (важно для соответствия параметров тексту SQL).
        var result = new Dictionary<string, object?>(source.Count);
        foreach (var (key, value) in source)
            result[key] = QueryEndpoints.FromJsonElement(value);
        return result;
    }
}
