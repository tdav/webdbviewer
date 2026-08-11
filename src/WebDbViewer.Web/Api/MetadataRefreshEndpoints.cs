using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>Тело запроса ручного обновления метаданных.</summary>
/// <param name="DsId">Идентификатор датасорса.</param>
/// <param name="Schema">Схема; null — схема по умолчанию для датасорса.</param>
/// <param name="Db">Выбранная в тулбаре база сервера; null — база из настроек подключения.</param>
public sealed record MetadataRefreshRequest(Guid DsId, string? Schema = null, string? Db = null);

/// <summary>
/// Ручное обновление кэша метаданных. Нужно после DDL: без него подсказки
/// живут по TTL и новых объектов не видят.
/// </summary>
public static class MetadataRefreshEndpoints
{
    public static IEndpointRouteBuilder MapMetadataRefreshApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/metadata/refresh", RefreshAsync).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Сбрасывает снапшот схемы и запускает прогрев в фоне. Возвращает 202 сразу:
    /// интроспекция большой схемы длится секунды, держать ради неё ответ незачем.
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        MetadataRefreshRequest request,
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

        var schema = CompletionEndpoints.DefaultSchemaFor(config, request.Schema);
        if (string.IsNullOrWhiteSpace(schema))
            return Results.Accepted();

        await metadata.InvalidateAsync(request.DsId, request.Db, schema, ct);

        var logger = loggerFactory.CreateLogger(typeof(MetadataRefreshEndpoints));
        // Токен запроса не передаётся: прогрев переживает завершение HTTP-ответа.
        _ = Task.Run(async () =>
        {
            try
            {
                await metadata.WarmupAsync(request.DsId, request.Db, [schema], CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Обновление метаданных {Schema} датасорса {DataSourceId} не удалось",
                    schema, request.DsId);
            }
        }, CancellationToken.None);

        return Results.Accepted();
    }
}
