using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using WebDbViewer.Core;

namespace WebDbViewer.Providers.Postgres;

/// <summary>Провайдер PostgreSQL: Npgsql + интроспекция pg_catalog.</summary>
public sealed partial class PostgresProvider : IDbProvider
{
    /// <summary>Категории («папки») дерева навигатора внутри схемы.</summary>
    private static readonly (string Key, string Title, DbObjectType Type)[] Categories =
    [
        ("Tables", "Таблицы", DbObjectType.Table),
        ("Views", "Представления", DbObjectType.View),
        ("MaterializedViews", "Материализованные представления", DbObjectType.MaterializedView),
        ("Sequences", "Последовательности", DbObjectType.Sequence),
        ("Functions", "Функции", DbObjectType.Function),
        ("Procedures", "Процедуры", DbObjectType.Procedure),
    ];

    /// <summary>Зарезервированные слова PostgreSQL (минимально необходимый набор) — всегда квотируются.</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both",
        "case", "cast", "check", "collate", "column", "constraint", "create", "current_catalog",
        "current_date", "current_role", "current_time", "current_timestamp", "current_user",
        "default", "deferrable", "desc", "distinct", "do", "else", "end", "except", "false",
        "fetch", "for", "foreign", "from", "grant", "group", "having", "in", "initially",
        "intersect", "into", "lateral", "leading", "limit", "localtime", "localtimestamp",
        "not", "null", "offset", "on", "only", "or", "order", "placing", "primary", "references",
        "returning", "select", "session_user", "some", "symmetric", "table", "then", "to",
        "trailing", "true", "union", "unique", "user", "using", "variadic", "when", "where",
        "window", "with",
    };

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();

    public DbKind Kind => DbKind.Postgres;

    public string? RowAddressPseudoColumn => "ctid";

    // ---------------------------------------------------------------- Подключение

    public string BuildConnectionString(DataSourceConfig config, string plainPassword)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = config.Host,
            Port = config.Port > 0 ? config.Port : 5432,
            Database = config.Database,
            Username = config.Username,
            Password = plainPassword,
            // SSL Mode: Require при включённом SSL, иначе Prefer (постараться, но не требовать).
            SslMode = config.UseSsl ? SslMode.Require : SslMode.Prefer,
            Pooling = true,
            Timeout = config.ConnectTimeoutSeconds > 0 ? config.ConnectTimeoutSeconds : 15,
            CommandTimeout = config.CommandTimeoutSeconds > 0 ? config.CommandTimeoutSeconds : 120,
            MaxPoolSize = Math.Max(1, config.MaxPoolSizePerUser),
            ApplicationName = "WebDbViewer",
        };

        if (config.Extra is not null)
        {
            foreach (var (key, value) in config.Extra)
                builder[key] = value;
        }

        return builder.ConnectionString;
    }

    public async Task<DbConnection> OpenConnectionAsync(DataSourceConfig config, string plainPassword, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(BuildConnectionString(config, plainPassword));
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            if (config.ReadOnly)
            {
                // Read-only флаг датасорса: запрещаем запись на уровне сессии.
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "SET default_transaction_read_only = on";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> TestConnectionAsync(DataSourceConfig config, string plainPassword, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(config, plainPassword, ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version()";
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result?.ToString() ?? "PostgreSQL";
    }

    // ---------------------------------------------------------------- Интроспекция

    public async Task<IReadOnlyList<string>> GetSchemasAsync(DbConnection connection, bool includeSystem, CancellationToken ct)
    {
        var sql = includeSystem
            ? "SELECT nspname FROM pg_catalog.pg_namespace ORDER BY nspname"
            : """
              SELECT nspname FROM pg_catalog.pg_namespace
              WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
              ORDER BY nspname
              """;

        var result = new List<string>();
        await using var cmd = CreateCommand(connection, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<DbObjectNode>> GetChildrenAsync(
        DbConnection connection, IReadOnlyList<string> parentPath, bool includeSystem, CancellationToken ct)
    {
        switch (parentPath.Count)
        {
            case 0:
                return await GetSchemaNodesAsync(connection, includeSystem, ct).ConfigureAwait(false);
            case 1:
                // Внутри схемы — фиксированные категории.
                return Categories
                    .Select(c => new DbObjectNode
                    {
                        Name = c.Key,
                        Type = c.Type,
                        Schema = parentPath[0],
                        HasChildren = true,
                        Attributes = new Dictionary<string, string> { ["kind"] = "folder", ["title"] = c.Title },
                    })
                    .ToList();
            case 2:
                return await GetCategoryChildrenAsync(connection, parentPath[0], parentPath[1], ct).ConfigureAwait(false);
            case 3:
                return await GetColumnNodesAsync(connection, parentPath[0], parentPath[2], ct).ConfigureAwait(false);
            default:
                return [];
        }
    }

    private async Task<IReadOnlyList<DbObjectNode>> GetSchemaNodesAsync(DbConnection connection, bool includeSystem, CancellationToken ct)
    {
        var sql = """
            SELECT n.nspname,
                   (n.nspname LIKE 'pg\_%' OR n.nspname = 'information_schema') AS is_system,
                   obj_description(n.oid, 'pg_namespace') AS comment
            FROM pg_catalog.pg_namespace n
            """ + (includeSystem ? "" : " WHERE n.nspname NOT LIKE 'pg\\_%' AND n.nspname <> 'information_schema'")
            + " ORDER BY n.nspname";

        var result = new List<DbObjectNode>();
        await using var cmd = CreateCommand(connection, sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DbObjectNode
            {
                Name = reader.GetString(0),
                Type = DbObjectType.Schema,
                HasChildren = true,
                IsSystem = reader.GetBoolean(1),
                Comment = reader.IsDBNull(2) ? null : reader.GetString(2),
            });
        }
        return result;
    }

    private async Task<IReadOnlyList<DbObjectNode>> GetCategoryChildrenAsync(
        DbConnection connection, string schema, string category, CancellationToken ct)
    {
        switch (category)
        {
            case "Tables":
                return await QueryRelationsAsync(connection, schema, "('r','p')", DbObjectType.Table, hasChildren: true, ct).ConfigureAwait(false);
            case "Views":
                return await QueryRelationsAsync(connection, schema, "('v')", DbObjectType.View, hasChildren: true, ct).ConfigureAwait(false);
            case "MaterializedViews":
                return await QueryRelationsAsync(connection, schema, "('m')", DbObjectType.MaterializedView, hasChildren: true, ct).ConfigureAwait(false);
            case "Sequences":
                return await QueryRelationsAsync(connection, schema, "('S')", DbObjectType.Sequence, hasChildren: false, ct).ConfigureAwait(false);
            case "Functions":
                return await QueryRoutineNodesAsync(connection, schema, "f", DbObjectType.Function, ct).ConfigureAwait(false);
            case "Procedures":
                return await QueryRoutineNodesAsync(connection, schema, "p", DbObjectType.Procedure, ct).ConfigureAwait(false);
            default:
                return [];
        }
    }

    private static async Task<IReadOnlyList<DbObjectNode>> QueryRelationsAsync(
        DbConnection connection, string schema, string relkindList, DbObjectType type, bool hasChildren, CancellationToken ct)
    {
        // relkindList — константа из кода (не пользовательский ввод).
        var sql = $"""
            SELECT c.relname, obj_description(c.oid, 'pg_class') AS comment
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relkind IN {relkindList}
            ORDER BY c.relname
            """;

        var result = new List<DbObjectNode>();
        await using var cmd = CreateCommand(connection, sql, ("schema", schema));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DbObjectNode
            {
                Name = reader.GetString(0),
                Type = type,
                Schema = schema,
                HasChildren = hasChildren,
                Comment = reader.IsDBNull(1) ? null : reader.GetString(1),
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<DbObjectNode>> QueryRoutineNodesAsync(
        DbConnection connection, string schema, string prokind, DbObjectType type, CancellationToken ct)
    {
        var sql = """
            SELECT p.proname,
                   pg_catalog.pg_get_function_arguments(p.oid) AS args,
                   obj_description(p.oid, 'pg_proc') AS comment
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.prokind = @prokind
            ORDER BY p.proname
            """;

        var result = new List<DbObjectNode>();
        await using var cmd = CreateCommand(connection, sql, ("schema", schema), ("prokind", prokind));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var args = reader.IsDBNull(1) ? null : reader.GetString(1);
            result.Add(new DbObjectNode
            {
                Name = reader.GetString(0),
                Type = type,
                Schema = schema,
                Comment = reader.IsDBNull(2) ? null : reader.GetString(2),
                Attributes = args is null ? null : new Dictionary<string, string> { ["arguments"] = args },
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<DbObjectNode>> GetColumnNodesAsync(
        DbConnection connection, string schema, string table, CancellationToken ct)
    {
        var sql = """
            SELECT a.attname,
                   pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
                   NOT a.attnotnull AS is_nullable,
                   col_description(c.oid, a.attnum) AS comment
            FROM pg_catalog.pg_attribute a
            JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """;

        var result = new List<DbObjectNode>();
        await using var cmd = CreateCommand(connection, sql, ("schema", schema), ("table", table));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DbObjectNode
            {
                Name = reader.GetString(0),
                Type = DbObjectType.Column,
                Schema = schema,
                Comment = reader.IsDBNull(3) ? null : reader.GetString(3),
                Attributes = new Dictionary<string, string>
                {
                    ["dataType"] = reader.GetString(1),
                    ["nullable"] = reader.GetBoolean(2) ? "true" : "false",
                },
            });
        }
        return result;
    }

    public async Task<SchemaSnapshot> LoadSchemaSnapshotAsync(DbConnection connection, string schemaName, CancellationToken ct)
    {
        var tables = await LoadTablesAsync(connection, schemaName, tableFilter: null, ct).ConfigureAwait(false);
        var routines = await LoadRoutinesAsync(connection, schemaName, ct).ConfigureAwait(false);
        var version = await GetSchemaVersionAsync(connection, schemaName, ct).ConfigureAwait(false);

        return new SchemaSnapshot
        {
            SchemaName = schemaName,
            Tables = tables,
            Routines = routines,
            LoadedAt = DateTimeOffset.UtcNow,
            VersionHash = version,
        };
    }

    public async Task<TableInfo> GetTableInfoAsync(DbConnection connection, string schema, string table, CancellationToken ct)
    {
        var tables = await LoadTablesAsync(connection, schema, table, ct).ConfigureAwait(false);
        return tables.FirstOrDefault()
            ?? throw new InvalidOperationException($"Таблица «{schema}.{table}» не найдена.");
    }

    /// <summary>Загружает таблицы/представления схемы с колонками, PK и FK. tableFilter — одна таблица или все (null).</summary>
    private static async Task<IReadOnlyList<TableInfo>> LoadTablesAsync(
        DbConnection connection, string schema, string? tableFilter, CancellationToken ct)
    {
        var filterSql = tableFilter is null ? "" : " AND c.relname = @table";
        (string, object?)[] Params() => tableFilter is null
            ? [("schema", schema)]
            : [("schema", schema), ("table", tableFilter)];

        // 1. Отношения: таблицы (в т.ч. секционированные), представления, материализованные представления.
        var relations = new List<(string Name, DbObjectType Type, string? Comment)>();
        {
            var sql = $"""
                SELECT c.relname, c.relkind::text, obj_description(c.oid, 'pg_class')
                FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = @schema AND c.relkind IN ('r','p','v','m'){filterSql}
                ORDER BY c.relname
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var kind = reader.GetString(1) switch
                {
                    "v" => DbObjectType.View,
                    "m" => DbObjectType.MaterializedView,
                    _ => DbObjectType.Table,
                };
                relations.Add((reader.GetString(0), kind, reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        // 2. Первичные ключи (pg_constraint contype='p').
        var pkByTable = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT c.relname,
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum) AS cols
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class c ON c.oid = con.conrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE con.contype = 'p' AND n.nspname = @schema{filterSql}
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(1))
                    pkByTable[reader.GetString(0)] = reader.GetFieldValue<string[]>(1);
            }
        }

        // 3. Колонки всех отношений схемы.
        var columnsByTable = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT c.relname,
                       a.attname,
                       pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
                       NOT a.attnotnull AS is_nullable,
                       a.attnum,
                       pg_catalog.pg_get_expr(d.adbin, d.adrelid) AS default_expr,
                       (a.attgenerated <> '' OR a.attidentity <> '') AS is_generated,
                       col_description(c.oid, a.attnum) AS comment
                FROM pg_catalog.pg_attribute a
                JOIN pg_catalog.pg_class c ON c.oid = a.attrelid
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
                WHERE n.nspname = @schema AND c.relkind IN ('r','p','v','m')
                  AND a.attnum > 0 AND NOT a.attisdropped{filterSql}
                ORDER BY c.relname, a.attnum
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var tableName = reader.GetString(0);
                var columnName = reader.GetString(1);
                var pk = pkByTable.TryGetValue(tableName, out var pkCols) && pkCols.Contains(columnName, StringComparer.Ordinal);
                if (!columnsByTable.TryGetValue(tableName, out var list))
                    columnsByTable[tableName] = list = [];
                list.Add(new ColumnInfo
                {
                    Name = columnName,
                    DataType = reader.GetString(2),
                    IsNullable = reader.GetBoolean(3),
                    OrdinalPosition = reader.GetInt16(4),
                    DefaultValue = reader.IsDBNull(5) ? null : reader.GetString(5),
                    IsGenerated = reader.GetBoolean(6),
                    Comment = reader.IsDBNull(7) ? null : reader.GetString(7),
                    IsPrimaryKey = pk,
                });
            }
        }

        // 4. Внешние ключи (contype='f') с колонками в порядке следования.
        var fkByTable = new Dictionary<string, List<ForeignKeyInfo>>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT con.conname,
                       fn.nspname AS from_schema, fc.relname AS from_table,
                       tn.nspname AS to_schema, tc.relname AS to_table,
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum) AS from_cols,
                       (SELECT array_agg(a.attname ORDER BY k.ord)
                          FROM unnest(con.confkey) WITH ORDINALITY AS k(attnum, ord)
                          JOIN pg_catalog.pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum) AS to_cols
                FROM pg_catalog.pg_constraint con
                JOIN pg_catalog.pg_class fc ON fc.oid = con.conrelid
                JOIN pg_catalog.pg_namespace fn ON fn.oid = fc.relnamespace
                JOIN pg_catalog.pg_class tc ON tc.oid = con.confrelid
                JOIN pg_catalog.pg_namespace tn ON tn.oid = tc.relnamespace
                WHERE con.contype = 'f' AND fn.nspname = @schema{(tableFilter is null ? "" : " AND fc.relname = @table")}
                ORDER BY fc.relname, con.conname
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var fromTable = reader.GetString(2);
                if (!fkByTable.TryGetValue(fromTable, out var list))
                    fkByTable[fromTable] = list = [];
                list.Add(new ForeignKeyInfo
                {
                    ConstraintName = reader.GetString(0),
                    FromSchema = reader.GetString(1),
                    FromTable = fromTable,
                    ToSchema = reader.GetString(3),
                    ToTable = reader.GetString(4),
                    FromColumns = reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
                    ToColumns = reader.IsDBNull(6) ? [] : reader.GetFieldValue<string[]>(6),
                });
            }
        }

        return relations
            .Select(r => new TableInfo
            {
                Schema = schema,
                Name = r.Name,
                Type = r.Type,
                Comment = r.Comment,
                Columns = columnsByTable.TryGetValue(r.Name, out var cols) ? cols : [],
                PrimaryKeyColumns = pkByTable.TryGetValue(r.Name, out var pk) ? pk : [],
                ForeignKeys = fkByTable.TryGetValue(r.Name, out var fks) ? fks : [],
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection connection, string schema, CancellationToken ct)
    {
        var sql = """
            SELECT p.proname, p.prokind::text,
                   pg_catalog.pg_get_function_result(p.oid) AS return_type,
                   pg_catalog.pg_get_function_arguments(p.oid) AS args,
                   obj_description(p.oid, 'pg_proc') AS comment
            FROM pg_catalog.pg_proc p
            JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @schema AND p.prokind IN ('f','p')
            ORDER BY p.proname
            """;

        var result = new List<RoutineInfo>();
        await using var cmd = CreateCommand(connection, sql, ("schema", schema));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RoutineInfo
            {
                Schema = schema,
                Name = reader.GetString(0),
                Type = reader.GetString(1) == "p" ? DbObjectType.Procedure : DbObjectType.Function,
                ReturnType = reader.IsDBNull(2) ? null : reader.GetString(2),
                ArgumentsSignature = reader.IsDBNull(3) ? null : reader.GetString(3),
                Comment = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }
        return result;
    }

    public async Task<string> GetSchemaVersionAsync(DbConnection connection, string schemaName, CancellationToken ct)
    {
        // Дешёвая версия DDL-состояния схемы: количество отношений + максимальный xmin строк pg_class.
        var sql = """
            SELECT count(*)::bigint AS cnt,
                   coalesce(max(c.xmin::text::bigint), 0) AS max_xmin
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema
            """;

        await using var cmd = CreateCommand(connection, sql, ("schema", schemaName));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long count = 0, maxXmin = 0;
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            count = reader.GetInt64(0);
            maxXmin = reader.GetInt64(1);
        }
        return ComputeHash($"pg:{schemaName}:{count}:{maxXmin}");
    }

    // ---------------------------------------------------------------- Генерация SQL

    public string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        // Правило PostgreSQL: неквотированные идентификаторы приводятся к lowercase.
        // Квотируем всё, что не соответствует ^[a-z_][a-z0-9_]*$ или является зарезервированным словом.
        if (SafeIdentifierRegex().IsMatch(identifier) && !ReservedWords.Contains(identifier))
            return identifier;
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public string BuildSelectPageSql(TableInfo table, DataPageRequest request)
    {
        var hasPk = table.PrimaryKeyColumns.Count > 0;

        // Ключ keyset-пагинации: явный OrderBy (+PK как tiebreaker), иначе PK, иначе ctid.
        List<string> keyColumns;
        if (!string.IsNullOrWhiteSpace(request.OrderBy))
        {
            keyColumns = [request.OrderBy!];
            keyColumns.AddRange(table.PrimaryKeyColumns.Where(c => !string.Equals(c, request.OrderBy, StringComparison.Ordinal)));
            if (!hasPk)
                keyColumns.Add("ctid");
        }
        else
        {
            keyColumns = hasPk ? [.. table.PrimaryKeyColumns] : ["ctid"];
        }

        var quotedKeys = keyColumns.Select(c => c == "ctid" ? "ctid" : QuoteIdentifier(c)).ToList();

        var sb = new StringBuilder("SELECT ");
        if (!hasPk)
            sb.Append("ctid, "); // псевдоколонка адреса строки для таблиц без PK
        sb.Append(table.Columns.Count > 0
            ? string.Join(", ", table.Columns.Select(c => QuoteIdentifier(c.Name)))
            : "*");
        sb.Append(" FROM ").Append(QuoteIdentifier(table.Schema)).Append('.').Append(QuoteIdentifier(table.Name));

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.WhereFilter))
            where.Add($"({request.WhereFilter})");
        if (request.After is { Count: > 0 })
        {
            // Кортежное сравнение: WHERE (k1, k2) > (@after0, @after1) — эффективный keyset.
            var op = request.OrderDescending ? "<" : ">";
            var placeholders = string.Join(", ", Enumerable.Range(0, quotedKeys.Count).Select(i => $"@after{i}"));
            where.Add($"({string.Join(", ", quotedKeys)}) {op} ({placeholders})");
        }
        if (where.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", where));

        var direction = request.OrderDescending ? " DESC" : "";
        sb.Append(" ORDER BY ").Append(string.Join(", ", quotedKeys.Select(k => k + direction)));
        sb.Append(" LIMIT ").Append(Math.Clamp(request.Limit, 1, 10_000));
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Вспомогательное

    private static DbCommand CreateCommand(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
