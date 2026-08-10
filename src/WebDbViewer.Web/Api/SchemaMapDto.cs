using System.Text.Json.Serialization;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>Column of a schema snapshot sent to the browser. Keys are short: the whole schema travels over the wire.</summary>
public sealed record SchemaMapColumn(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("d")] string DataType,
    [property: JsonPropertyName("pk")] bool IsPrimaryKey,
    [property: JsonPropertyName("nl")] bool IsNullable,
    [property: JsonPropertyName("cm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>Table or view of a schema snapshot.</summary>
public sealed record SchemaMapTable(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("c")] IReadOnlyList<SchemaMapColumn> Columns,
    [property: JsonPropertyName("cm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>Routine of a schema snapshot.</summary>
public sealed record SchemaMapRoutine(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Signature,
    [property: JsonPropertyName("cm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>Schema snapshot for client-side completion.</summary>
public sealed record SchemaMapResponse(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("tables")] IReadOnlyList<SchemaMapTable> Tables,
    [property: JsonPropertyName("routines")] IReadOnlyList<SchemaMapRoutine> Routines);

/// <summary>
/// Projection of <see cref="SchemaSnapshot"/> onto the wire format of /api/completion/schema-map.
/// Pure functions only — the endpoint stays a thin wrapper and this stays unit-testable.
/// </summary>
public static class SchemaMapDto
{
    /// <summary>Above these sizes columns are dropped: a full snapshot would cost megabytes per editor open.</summary>
    public const int MaxTables = 2000;
    public const int MaxColumns = 50000;

    public static SchemaMapResponse From(SchemaSnapshot snapshot)
    {
        var totalColumns = snapshot.Tables.Sum(t => t.Columns.Count);
        var partial = snapshot.Tables.Count > MaxTables || totalColumns > MaxColumns;

        var tables = snapshot.Tables
            .Select(t => new SchemaMapTable(
                t.Name,
                TypeName(t.Type),
                partial ? [] : t.Columns.Select(Column).ToList(),
                Trimmed(t.Comment)))
            .ToList();

        var routines = snapshot.Routines
            .Select(r => new SchemaMapRoutine(
                r.Name,
                r.Type == DbObjectType.Procedure ? "procedure" : "function",
                Trimmed(r.ArgumentsSignature),
                Trimmed(r.Comment)))
            .ToList();

        return new SchemaMapResponse(snapshot.SchemaName, partial, tables, routines);
    }

    /// <summary>ETag from the snapshot version; null when the provider does not report one.</summary>
    public static string? ETagFor(SchemaSnapshot snapshot) =>
        string.IsNullOrEmpty(snapshot.VersionHash) ? null : "\"" + snapshot.VersionHash + "\"";

    /// <summary>True when the client already holds this exact snapshot.</summary>
    public static bool IsNotModified(string? etag, string? ifNoneMatch)
    {
        if (etag is null || string.IsNullOrWhiteSpace(ifNoneMatch))
            return false;
        foreach (var candidate in ifNoneMatch.Split(','))
        {
            var value = candidate.Trim();
            if (value.StartsWith("W/", StringComparison.Ordinal))
                value = value[2..];
            if (string.Equals(value, etag, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static SchemaMapColumn Column(ColumnInfo c) =>
        new(c.Name, c.DataType, c.IsPrimaryKey, c.IsNullable, Trimmed(c.Comment));

    private static string TypeName(DbObjectType type) => type switch
    {
        DbObjectType.View => "view",
        DbObjectType.MaterializedView => "mview",
        _ => "table",
    };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
