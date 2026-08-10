using WebDbViewer.Completion;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>Тело запроса автодополнения из редактора (CodeMirror 6).</summary>
/// <param name="DsId">Идентификатор датасорса — определяет диалект.</param>
/// <param name="Sql">Полный текст в редакторе.</param>
/// <param name="CaretOffset">Позиция каретки (смещение в символах).</param>
/// <param name="DefaultSchema">Текущая схема (по умолчанию public для PostgreSQL).</param>
public sealed record CompletionApiRequest(Guid DsId, string Sql, int CaretOffset, string? DefaultSchema = null);

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
        app.MapGet("/api/completion/schema-map", SchemaMapAsync).RequireAuthorization();
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
    /// Снапшот схемы для клиентского автодополнения. Он же прогрев кэша: построение ответа
    /// заполняет MetadataCache, отдельный warmup-запрос не нужен.
    /// 204 — схему определить не удалось или интроспекция упала: редактор просто работает
    /// без локального кэша, подсказки идут с сервера как раньше.
    /// </summary>
    private static async Task<IResult> SchemaMapAsync(
        Guid dsId,
        string? schema,
        HttpContext http,
        IDataSourceStore dataSourceStore,
        IMetadataCache metadata,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (dsId == Guid.Empty)
            return Results.BadRequest(new { error = "Не задан датасорс." });

        var config = await dataSourceStore.GetAsync(dsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var schemaName = DefaultSchemaFor(config, schema);
        if (string.IsNullOrWhiteSpace(schemaName))
            return Results.NoContent();

        SchemaSnapshot snapshot;
        try
        {
            snapshot = await metadata.GetSchemaAsync(dsId, schemaName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory.CreateLogger(typeof(CompletionEndpoints))
                .LogWarning(ex, "Снапшот схемы {Schema} датасорса {DataSourceId} получить не удалось", schemaName, dsId);
            return Results.NoContent();
        }

        var etag = SchemaMapDto.ETagFor(snapshot);
        if (SchemaMapDto.IsNotModified(etag, http.Request.Headers.IfNoneMatch))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        if (etag is not null)
            http.Response.Headers.ETag = etag;
        return Results.Json(SchemaMapDto.From(snapshot));
    }
}
