using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;

namespace WebDbViewer.Web.Api;

/// <summary>
/// Endpoint генерации DDL объектов БД.
/// ВАЖНО (для владельца Program.cs): перед app.MapDdlApi() необходимо зарегистрировать:
///   builder.Services.AddPostgresDdl();   // IDdlGenerator для PostgreSQL
///   builder.Services.AddOracleDdl();     // IDdlGenerator для Oracle
///   builder.Services.AddDbSessions();    // IDbSessionManager, IDataSourceStore (уже есть в MVP)
/// </summary>
public static class DdlEndpoints
{
    public static IEndpointRouteBuilder MapDdlApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/ddl", GetDdlAsync);
        return app;
    }

    // ---------------------------------------------------------------- GET /api/ddl?ds=&schema=&name=&type=

    /// <summary>
    /// Текст DDL объекта. type: table | view | matview | index | function | procedure | package.
    /// Ответ — text/plain (DDL) либо JSON с ошибкой.
    /// </summary>
    private static async Task<IResult> GetDdlAsync(
        HttpContext http,
        IDbSessionManager sessionManager,
        IDataSourceStore dataSourceStore,
        IEnumerable<IDdlGenerator> generators,
        Guid ds,
        string? schema,
        string? name,
        string? type,
        CancellationToken ct)
    {
        if (ds == Guid.Empty || string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type))
            return Results.BadRequest(new { error = "Не заданы параметры ds, schema, name или type." });

        var config = await dataSourceStore.GetAsync(ds, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var generator = generators.FirstOrDefault(g => g.Kind == config.Kind);
        if (generator is null)
            return Results.Problem($"Генератор DDL для «{config.Kind}» не зарегистрирован.", statusCode: 500);

        var userName = http.User.Identity?.Name ?? "anonymous";
        var session = await sessionManager.GetOrCreateAsync(userName, ds, ct);

        try
        {
            var ddl = type.ToLowerInvariant() switch
            {
                "table" => await generator.GetTableDdlAsync(session.Connection, schema, name, ct),
                "view" or "matview" or "materializedview"
                    => await generator.GetViewDdlAsync(session.Connection, schema, name, ct),
                "index" => await generator.GetIndexDdlAsync(session.Connection, schema, name, ct),
                "function" => await generator.GetRoutineDdlAsync(session.Connection, schema, name, DbObjectType.Function, ct),
                "procedure" => await generator.GetRoutineDdlAsync(session.Connection, schema, name, DbObjectType.Procedure, ct),
                "package" => await generator.GetRoutineDdlAsync(session.Connection, schema, name, DbObjectType.Package, ct),
                _ => null,
            };

            if (ddl is null)
                return Results.BadRequest(new { error = $"Неизвестный тип объекта: «{type}»." });

            return Results.Text(ddl, "text/plain; charset=utf-8");
        }
        catch (DdlObjectNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException)
        {
            return Results.BadRequest(new { error = $"Ошибка получения DDL: {ex.Message}" });
        }
    }
}
