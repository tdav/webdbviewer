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
        string? db,
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
        var session = await sessionManager.GetOrCreateAsync(userName, ds, db, ct);

        var task = GetDdlTextAsync(generator, session.Connection, schema, name, type, ct);
        if (task is null)
            return Results.BadRequest(new { error = $"Неизвестный тип объекта: «{type}»." });

        try
        {
            return Results.Text(await task, "text/plain; charset=utf-8");
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

    /// <summary>
    /// DDL объекта по строковому типу из дерева навигатора; null — тип не поддерживается.
    /// Общая точка для /api/ddl и вкладки редактора с исходником.
    /// </summary>
    public static Task<string>? GetDdlTextAsync(
        IDdlGenerator generator,
        System.Data.Common.DbConnection connection,
        string schema,
        string name,
        string type,
        CancellationToken ct) => type.ToLowerInvariant() switch
        {
            "table" => generator.GetTableDdlAsync(connection, schema, name, ct),
            "view" or "matview" or "materializedview"
                => generator.GetViewDdlAsync(connection, schema, name, ct),
            "index" => generator.GetIndexDdlAsync(connection, schema, name, ct),
            "function" => generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Function, ct),
            "procedure" => generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Procedure, ct),
            "package" => generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Package, ct),
            "trigger" => generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Trigger, ct),
            "type" => generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Type, ct),
            _ => null,
        };
}
