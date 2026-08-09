using System.Data.Common;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Services;

/// <summary>
/// Область видимости схем датасорса. Если <see cref="DataSourceConfig.AllowAllSchemas"/> = false,
/// навигатор, поиск и список схем ограничиваются схемой подключения:
/// Oracle — схема текущего пользователя (USER), PostgreSQL — схемы из search_path.
/// Это ограничение видимости в UI, а не разграничение прав доступа к данным.
/// </summary>
public static class SchemaScope
{
    /// <summary>
    /// Возвращает набор разрешённых схем либо <c>null</c>, если ограничений нет
    /// (<see cref="DataSourceConfig.AllowAllSchemas"/> = true).
    /// </summary>
    public static async Task<IReadOnlySet<string>?> ResolveAsync(
        DataSourceConfig config, DbConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(connection);

        if (config.AllowAllSchemas)
            return null;

        var sql = config.Kind == DbKind.Oracle
            ? "SELECT USER FROM dual"
            : "SELECT unnest(current_schemas(false))";

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                    allowed.Add(reader.GetString(0));
            }
        }

        // Пустой search_path (или недоступный USER) — подстраховываемся именем пользователя датасорса,
        // иначе навигатор оказался бы полностью пустым.
        if (allowed.Count == 0 && !string.IsNullOrWhiteSpace(config.Username))
            allowed.Add(config.Username.Trim());

        return allowed;
    }

    /// <summary>Проверяет, попадает ли схема в разрешённую область (<c>null</c> — ограничений нет).</summary>
    public static bool IsAllowed(IReadOnlySet<string>? allowed, string? schema) =>
        allowed is null || (schema is not null && allowed.Contains(schema));

    /// <summary>
    /// Фильтрует узлы: узлы-схемы — по имени, остальные — по полю Schema.
    /// Узел без схемы скрывается: принадлежность к разрешённой области не подтверждена
    /// (в фильтруемых наборах — корень дерева и результаты поиска — таких узлов нет).
    /// </summary>
    public static IReadOnlyList<DbObjectNode> Filter(IReadOnlySet<string>? allowed, IReadOnlyList<DbObjectNode> nodes)
    {
        if (allowed is null)
            return nodes;

        return nodes
            .Where(n => n.Type == DbObjectType.Schema
                ? allowed.Contains(n.Name)
                : n.Schema is { } schema && allowed.Contains(schema))
            .ToList();
    }
}
