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
    /// Текст DDL объекта. type: table | view | matview | index | function | procedure | package |
    /// sequence | type | domain | foreigntable | aggregate | operator | collation | tsconfig |
    /// tsdictionary | trigger | rule | policy.
    /// qualifier уточняет объект среди одноимённых: таблица-владелец для trigger/rule/policy,
    /// сигнатура аргументов для перегруженной функции.
    /// Ответ — text/plain (DDL) либо JSON с ошибкой.
    /// </summary>
    private static async Task<IResult> GetDdlAsync(
        HttpContext http,
        IDbConnectionFactory connectionFactory,
        IDataSourceStore dataSourceStore,
        IEnumerable<IDdlGenerator> generators,
        Guid ds,
        string? schema,
        string? name,
        string? type,
        string? db,
        string? qualifier,
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

        try
        {
            // Чтение DDL — интроспекция каталога: сессия пользователя (её транзакция и её
            // единственное соединение) для этого не нужна.
            await using var connection = await connectionFactory.OpenAsync(config, db, ct);
            var ddl = await DdlText.GetAsync(generator, connection, schema, name, type, qualifier, ct);

            if (ddl is null)
                return Results.BadRequest(new { error = $"Неизвестный тип объекта: «{type}»." });

            return Results.Text(ddl, "text/plain; charset=utf-8");
        }
        catch (DdlObjectNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (NotSupportedException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException)
        {
            return Results.BadRequest(new { error = $"Ошибка получения DDL: {ex.Message}" });
        }
    }
}
