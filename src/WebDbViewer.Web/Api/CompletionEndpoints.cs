using WebDbViewer.Completion;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>Тело запроса автодополнения из редактора (CodeMirror 6).</summary>
/// <param name="DsId">Идентификатор датасорса — определяет диалект.</param>
/// <param name="Sql">Полный текст в редакторе.</param>
/// <param name="CaretOffset">Позиция каретки (смещение в символах).</param>
/// <param name="DefaultSchema">Текущая схема (по умолчанию public для PostgreSQL).</param>
public sealed record CompletionApiRequest(Guid DsId, string Sql, int CaretOffset, string? DefaultSchema = null);

/// <summary>Тело запроса прогрева кэша метаданных.</summary>
/// <param name="DsId">Идентификатор датасорса.</param>
/// <param name="Schema">Схема для прогрева; null — схема по умолчанию для датасорса.</param>
public sealed record CompletionWarmupRequest(Guid DsId, string? Schema = null);

/// <summary>
/// Endpoint автодополнения SQL.
/// ВАЖНО (для владельца Program.cs): перед app.MapCompletionApi() необходимо
/// builder.Services.AddSqlIntelliSense() (модуль WebDbViewer.Completion).
/// </summary>
public static class CompletionEndpoints
{
    public static IEndpointRouteBuilder MapCompletionApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/completion", CompleteAsync).RequireAuthorization();
        app.MapPost("/api/completion/signature", SignatureAsync).RequireAuthorization();
        app.MapPost("/api/completion/warmup", WarmupAsync).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Схема, в которой ищутся неквалифицированные имена. Для Oracle это схема пользователя
    /// подключения: имена в ALL_* хранятся в верхнем регистре, поэтому имя приводится к нему.
    /// </summary>
    public static string? DefaultSchemaFor(DataSourceConfig config, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested;
        return config.Kind == DbKind.Oracle ? config.Username.ToUpperInvariant() : "public";
    }

    private static async Task<IResult> CompleteAsync(
        CompletionApiRequest request,
        IDataSourceStore dataSourceStore,
        ICompletionEngine engine,
        CancellationToken ct)
    {
        if (request.DsId == Guid.Empty || request.Sql is null)
            return Results.BadRequest(new { error = "Не задан датасорс или текст запроса." });

        // Диалект определяется датасорсом.
        var config = await dataSourceStore.GetAsync(request.DsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var items = await engine.CompleteAsync(new CompletionRequest
        {
            DataSourceId = request.DsId,
            SqlText = request.Sql,
            CaretOffset = request.CaretOffset,
            DefaultSchema = DefaultSchemaFor(config, request.DefaultSchema),
        }, config.Kind, ct);

        return Results.Json(items);
    }

    /// <summary>
    /// Сигнатура функции, внутри скобок которой стоит каретка. Пустой ответ (204) —
    /// каретка не в вызове либо функция неизвестна; редактор просто не показывает подсказку.
    /// </summary>
    private static async Task<IResult> SignatureAsync(
        CompletionApiRequest request,
        IDataSourceStore dataSourceStore,
        ISemanticCompletionEngine engine,
        CancellationToken ct)
    {
        if (request.DsId == Guid.Empty || request.Sql is null)
            return Results.BadRequest(new { error = "Не задан датасорс или текст запроса." });

        var config = await dataSourceStore.GetAsync(request.DsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var signature = await engine.DescribeSignatureAsync(new CompletionRequest
        {
            DataSourceId = request.DsId,
            SqlText = request.Sql,
            CaretOffset = request.CaretOffset,
            DefaultSchema = DefaultSchemaFor(config, request.DefaultSchema),
        }, config.Kind, ct);

        return signature is null ? Results.NoContent() : Results.Json(signature);
    }

    /// <summary>
    /// Прогрев кэша метаданных, чтобы первый вызов автодополнения не ждал интроспекцию схемы.
    /// Возвращает 202 сразу: прогрев идёт в фоне, его сбой на редактор не влияет.
    /// </summary>
    private static async Task<IResult> WarmupAsync(
        CompletionWarmupRequest request,
        IDataSourceStore dataSourceStore,
        IMetadataCache metadata,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (request.DsId == Guid.Empty)
            return Results.BadRequest(new { error = "Не задан датасорс." });

        var config = await dataSourceStore.GetAsync(request.DsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var schema = DefaultSchemaFor(config, request.Schema);
        if (string.IsNullOrWhiteSpace(schema))
            return Results.Accepted();

        var logger = loggerFactory.CreateLogger(typeof(CompletionEndpoints));
        // Токен запроса не передаётся: прогрев переживает завершение HTTP-ответа.
        _ = Task.Run(async () =>
        {
            try
            {
                await metadata.WarmupAsync(request.DsId, [schema], CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Прогрев кэша метаданных {Schema} датасорса {DataSourceId} не удался",
                    schema, request.DsId);
            }
        }, CancellationToken.None);

        return Results.Accepted();
    }
}
