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

    /// <summary>
    /// Исходник PL/SQL-объекта из ALL_SOURCE, а не из DBMS_METADATA: текст получается ровно таким,
    /// каким его компилировали, не требует SELECT_CATALOG_ROLE и разделён «/» — то есть пригоден
    /// и для просмотра, и для правки с последующим выполнением в редакторе.
    /// </summary>
    public Task<string> GetRoutineDdlAsync(
        DbConnection connection, string schema, string routine, DbObjectType routineType, CancellationToken ct)
    {
        // Спецификация и тело — отдельные объекты словаря; в редакторе нужны оба, спецификация первой.
        string[] sourceTypes = routineType switch
        {
            DbObjectType.Procedure => ["PROCEDURE"],
            DbObjectType.Package => ["PACKAGE", "PACKAGE BODY"],
            DbObjectType.Type => ["TYPE", "TYPE BODY"],
            DbObjectType.Trigger => ["TRIGGER"],
            _ => ["FUNCTION"],
        };

        return GetSourceAsync(connection, schema, routine, sourceTypes, ct);
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

    // ---------------------------------------------------------------- ALL_SOURCE

    /// <summary>
    /// Собирает исходник из ALL_SOURCE. Секции (спецификация, тело) идут в порядке
    /// <paramref name="sourceTypes"/>, каждая получает «CREATE OR REPLACE» и терминатор «/»:
    /// без него сплиттер редактора не отличит конец PL/SQL-блока от «;» внутри него.
    /// </summary>
    private static async Task<string> GetSourceAsync(
        DbConnection connection, string schema, string name, string[] sourceTypes, CancellationToken ct)
    {
        // Порядок секций задаётся списком типов, а не алфавитом: тело всегда после спецификации.
        var order = string.Join(" ", sourceTypes.Select((t, i) => $"WHEN '{t}' THEN {i}"));
        var types = string.Join(", ", sourceTypes.Select((_, i) => $":t{i}"));
        var sql = $"""
            SELECT type, line, text
            FROM all_source
            WHERE owner = :owner AND name = :name AND type IN ({types})
            ORDER BY CASE type {order} END, line
            """;

        var parameters = new List<(string, object?)>
        {
            ("owner", Normalize(schema)),
            ("name", Normalize(name)),
        };
        parameters.AddRange(sourceTypes.Select((t, i) => ($"t{i}", (object?)t)));

        await using var cmd = CreateCommand(connection, sql, [.. parameters]);

        // Секции копим отдельно: заголовок каждой дописывается уже по собранному тексту.
        var sections = new List<(string Type, StringBuilder Text)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var type = reader.GetString(0);
            if (sections.Count == 0 || !string.Equals(sections[^1].Type, type, StringComparison.Ordinal))
                sections.Add((type, new StringBuilder()));
            if (!reader.IsDBNull(2))
                sections[^1].Text.Append(reader.GetString(2));
        }

        if (sections.Count == 0)
            throw new DdlObjectNotFoundException($"Исходник «{schema}.{name}» не найден.");

        var sb = new StringBuilder();
        foreach (var (type, text) in sections)
        {
            sb.Append(BuildCreateHeader(type, Normalize(schema), text.ToString().TrimEnd()));
            sb.Append("\n/\n\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Достраивает «CREATE OR REPLACE» к тексту из ALL_SOURCE и квалифицирует имя схемой.
    /// Без схемы CREATE ушёл бы в схему подключённого пользователя — объект тихо создался бы не там.
    /// </summary>
    private static string BuildCreateHeader(string sourceType, string owner, string body)
    {
        // Текст секции начинается с тех же ключевых слов, что и её тип: «PACKAGE BODY   ИМЯ …».
        var pos = 0;
        foreach (var word in sourceType.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            while (pos < body.Length && char.IsWhiteSpace(body[pos]))
                pos++;
            if (string.Compare(body, pos, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return "CREATE OR REPLACE " + body; // неожиданная форма заголовка — не трогаем текст
            pos += word.Length;
        }

        return $"CREATE OR REPLACE {sourceType} \"{owner}\".{body[pos..].TrimStart()}";
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
