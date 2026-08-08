using System.Data.Common;
using System.Text;
using Oracle.ManagedDataAccess.Client;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;

namespace WebDbViewer.Providers.Oracle;

/// <summary>
/// Генератор DDL Oracle: DBMS_METADATA.GET_DDL по типу объекта.
/// Для пакетов при недоступности DBMS_METADATA (нет прав) — fallback на сборку из ALL_SOURCE.
/// Имена объектов в словаре Oracle хранятся в верхнем регистре — приводим, если имя не квотированное.
/// </summary>
public sealed class OracleDdlGenerator : IDdlGenerator
{
    public DbKind Kind => DbKind.Oracle;

    public Task<string> GetTableDdlAsync(DbConnection connection, string schema, string table, CancellationToken ct)
        => GetMetadataDdlAsync(connection, "TABLE", schema, table, ct);

    public async Task<string> GetViewDdlAsync(DbConnection connection, string schema, string view, CancellationToken ct)
    {
        // Сначала обычное представление, затем материализованное.
        try
        {
            return await GetMetadataDdlAsync(connection, "VIEW", schema, view, ct).ConfigureAwait(false);
        }
        catch (DdlObjectNotFoundException)
        {
            return await GetMetadataDdlAsync(connection, "MATERIALIZED_VIEW", schema, view, ct).ConfigureAwait(false);
        }
    }

    public Task<string> GetIndexDdlAsync(DbConnection connection, string schema, string index, CancellationToken ct)
        => GetMetadataDdlAsync(connection, "INDEX", schema, index, ct);

    public async Task<string> GetRoutineDdlAsync(
        DbConnection connection, string schema, string routine, DbObjectType routineType, CancellationToken ct)
    {
        var metadataType = routineType switch
        {
            DbObjectType.Procedure => "PROCEDURE",
            DbObjectType.Package => "PACKAGE",
            _ => "FUNCTION",
        };

        if (routineType == DbObjectType.Package)
        {
            // DBMS_METADATA для пакетов часто недоступен (нет SELECT_CATALOG_ROLE) — fallback на ALL_SOURCE.
            try
            {
                return await GetMetadataDdlAsync(connection, metadataType, schema, routine, ct).ConfigureAwait(false);
            }
            catch (OracleException)
            {
                return await GetPackageFromAllSourceAsync(connection, schema, routine, ct).ConfigureAwait(false);
            }
        }

        return await GetMetadataDdlAsync(connection, metadataType, schema, routine, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- DBMS_METADATA

    private static async Task<string> GetMetadataDdlAsync(
        DbConnection connection, string objectType, string schema, string name, CancellationToken ct)
    {
        const string sql = "SELECT DBMS_METADATA.GET_DDL(:objType, :objName, :owner) FROM DUAL";

        await using var cmd = CreateCommand(connection, sql,
            ("objType", objectType), ("objName", Normalize(name)), ("owner", Normalize(schema)));

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(0))
                throw new DdlObjectNotFoundException($"Объект «{schema}.{name}» ({objectType}) не найден.");

            // CLOB читаем потоком (может быть большим).
            using var textReader = reader.GetTextReader(0);
            var ddl = await textReader.ReadToEndAsync(ct).ConfigureAwait(false);
            return ddl.Trim() + "\n";
        }
        catch (OracleException ex) when (ex.Number == 31603)
        {
            // ORA-31603: object not found in schema.
            throw new DdlObjectNotFoundException($"Объект «{schema}.{name}» ({objectType}) не найден.");
        }
    }

    // ---------------------------------------------------------------- Fallback: ALL_SOURCE (пакеты)

    private static async Task<string> GetPackageFromAllSourceAsync(
        DbConnection connection, string schema, string package, CancellationToken ct)
    {
        const string sql = """
            SELECT type, line, text
            FROM all_source
            WHERE owner = :owner AND name = :name AND type IN ('PACKAGE', 'PACKAGE BODY')
            ORDER BY CASE type WHEN 'PACKAGE' THEN 0 ELSE 1 END, line
            """;

        await using var cmd = CreateCommand(connection, sql,
            ("owner", Normalize(schema)), ("name", Normalize(package)));

        var sb = new StringBuilder();
        string? currentType = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var type = reader.GetString(0);
            if (!string.Equals(type, currentType, StringComparison.Ordinal))
            {
                // Новая секция (спецификация → тело): CREATE OR REPLACE + разделитель "/".
                if (currentType is not null)
                    sb.Append("/\n\n");
                sb.Append("CREATE OR REPLACE ");
                currentType = type;
            }
            if (!reader.IsDBNull(2))
                sb.Append(reader.GetString(2));
        }

        if (sb.Length == 0)
            throw new DdlObjectNotFoundException($"Пакет «{schema}.{package}» не найден.");

        if (sb[^1] != '\n')
            sb.Append('\n');
        sb.Append("/\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>Имя для словаря Oracle: неквотированные идентификаторы хранятся в UPPERCASE.</summary>
    private static string Normalize(string identifier)
        => identifier.StartsWith('"') ? identifier.Trim('"') : identifier.ToUpperInvariant();

    private static DbCommand CreateCommand(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (cmd is OracleCommand oracleCmd)
        {
            oracleCmd.BindByName = true; // именованные параметры :name
        }
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
