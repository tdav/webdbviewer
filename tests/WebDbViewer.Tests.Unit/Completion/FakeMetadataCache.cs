using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Completion;

/// <summary>Фейковый кэш метаданных: схема public с таблицами users и orders.</summary>
public sealed class FakeMetadataCache : IMetadataCache
{
    public static readonly Guid DsId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ColumnInfo Col(string name, string type, int pos, bool pk = false) => new()
    {
        Name = name,
        DataType = type,
        OrdinalPosition = pos,
        IsPrimaryKey = pk,
    };

    public static readonly SchemaSnapshot PublicSchema = new()
    {
        SchemaName = "public",
        Tables =
        [
            new TableInfo
            {
                Schema = "public",
                Name = "users",
                Type = DbObjectType.Table,
                Columns = [Col("id", "bigint", 1, pk: true), Col("name", "text", 2), Col("email", "text", 3)],
                PrimaryKeyColumns = ["id"],
            },
            new TableInfo
            {
                Schema = "public",
                Name = "orders",
                Type = DbObjectType.Table,
                Columns = [Col("id", "bigint", 1, pk: true), Col("user_id", "bigint", 2), Col("total", "numeric", 3)],
                PrimaryKeyColumns = ["id"],
            },
        ],
        Routines =
        [
            new RoutineInfo
            {
                Schema = "public",
                Name = "calc_total",
                Type = DbObjectType.Function,
                ReturnType = "numeric",
                ArgumentsSignature = "order_id bigint",
                Comment = "Сумма заказа с учётом скидок",
            },
            new RoutineInfo
            {
                Schema = "public",
                Name = "archive_orders",
                Type = DbObjectType.Procedure,
                ArgumentsSignature = "before date",
            },
        ],
        LoadedAt = DateTimeOffset.UtcNow,
    };

    public Task<SchemaSnapshot> GetSchemaAsync(Guid dataSourceId, string? database, string schemaName, CancellationToken ct)
    {
        if (dataSourceId != DsId || !string.Equals(schemaName, "public", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Схема не найдена: {schemaName}");
        return Task.FromResult(PublicSchema);
    }

    public Task<IReadOnlyList<SchemaSnapshot>> GetLoadedSchemasAsync(Guid dataSourceId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SchemaSnapshot>>([PublicSchema]);

    public Task WarmupAsync(Guid dataSourceId, string? database, IReadOnlyList<string> schemas, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<DbObjectNode>> SearchAsync(Guid dataSourceId, string query, int limit, CancellationToken ct)
    {
        var nodes = PublicSchema.Tables
            .Where(t => string.IsNullOrEmpty(query)
                        || t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Select(t => new DbObjectNode { Name = t.Name, Type = t.Type, Schema = t.Schema })
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<DbObjectNode>>(nodes);
    }

    public Task InvalidateAsync(Guid dataSourceId, string? database, string? schemaName, CancellationToken ct) =>
        Task.CompletedTask;
}
