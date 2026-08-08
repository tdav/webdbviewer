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
        return app;
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
            DefaultSchema = request.DefaultSchema,
        }, config.Kind, ct);

        return Results.Json(items);
    }
}
