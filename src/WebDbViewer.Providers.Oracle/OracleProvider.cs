using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using WebDbViewer.Core;

namespace WebDbViewer.Providers.Oracle;

/// <summary>Провайдер Oracle: ODP.NET Core + интроспекция ALL_* (всегда с фильтром по OWNER).</summary>
public sealed partial class OracleProvider : IDbProvider
{
    /// <summary>Категории («папки») дерева навигатора внутри схемы.</summary>
    private static readonly (string Key, string Title, DbObjectType Type)[] Categories =
    [
        ("Tables", "Таблицы", DbObjectType.Table),
        ("Views", "Представления", DbObjectType.View),
        ("MaterializedViews", "Материализованные представления", DbObjectType.MaterializedView),
        ("Indexes", "Индексы", DbObjectType.Index),
        ("Sequences", "Последовательности", DbObjectType.Sequence),
        ("Synonyms", "Синонимы", DbObjectType.Synonym),
        ("Functions", "Функции", DbObjectType.Function),
        ("Procedures", "Процедуры", DbObjectType.Procedure),
        ("Packages", "Пакеты", DbObjectType.Package),
        ("Types", "Типы", DbObjectType.Type),
        ("Triggers", "Триггеры", DbObjectType.Trigger),
        ("DbLinks", "Линки БД", DbObjectType.DbLink),
    ];

    /// <summary>Системные схемы Oracle (fallback-фильтр, если недоступен ALL_USERS.ORACLE_MAINTAINED).</summary>
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "SYS", "SYSTEM", "SYSAUX", "OUTLN", "XDB", "CTXSYS", "MDSYS", "ORDSYS", "ORDDATA",
        "ORDPLUGINS", "OLAPSYS", "LBACSYS", "DVSYS", "DVF", "WMSYS", "EXFSYS", "AUDSYS",
        "OJVMSYS", "GSMADMIN_INTERNAL", "APPQOSSYS", "DBSNMP", "ANONYMOUS", "DBSFWUSER",
        "REMOTE_SCHEDULER_AGENT", "SI_INFORMTN_SCHEMA", "XS$NULL", "DIP", "ORACLE_OCM",
        "GGSYS", "SYS$UMF", "MDDATA",
    };

    /// <summary>Зарезервированные слова Oracle (минимально необходимый набор) — всегда квотируются.</summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "ACCESS", "ADD", "ALL", "ALTER", "AND", "ANY", "AS", "ASC", "AUDIT", "BETWEEN", "BY",
        "CHAR", "CHECK", "CLUSTER", "COLUMN", "COMMENT", "COMPRESS", "CONNECT", "CREATE",
        "CURRENT", "DATE", "DECIMAL", "DEFAULT", "DELETE", "DESC", "DISTINCT", "DROP", "ELSE",
        "EXCLUSIVE", "EXISTS", "FILE", "FLOAT", "FOR", "FROM", "GRANT", "GROUP", "HAVING",
        "IDENTIFIED", "IMMEDIATE", "IN", "INCREMENT", "INDEX", "INITIAL", "INSERT", "INTEGER",
        "INTERSECT", "INTO", "IS", "LEVEL", "LIKE", "LOCK", "LONG", "MAXEXTENTS", "MINUS",
        "MLSLABEL", "MODE", "MODIFY", "NOAUDIT", "NOCOMPRESS", "NOT", "NOWAIT", "NULL",
        "NUMBER", "OF", "OFFLINE", "ON", "ONLINE", "OPTION", "OR", "ORDER", "PCTFREE", "PRIOR",
        "PUBLIC", "RAW", "RENAME", "RESOURCE", "REVOKE", "ROW", "ROWID", "ROWNUM", "ROWS",
        "SELECT", "SESSION", "SET", "SHARE", "SIZE", "SMALLINT", "START", "SUCCESSFUL",
        "SYNONYM", "SYSDATE", "TABLE", "THEN", "TO", "TRIGGER", "UID", "UNION", "UNIQUE",
        "UPDATE", "USER", "VALIDATE", "VALUES", "VARCHAR", "VARCHAR2", "VIEW", "WHENEVER",
        "WHERE", "WITH",
    };

    [GeneratedRegex(@"^[A-Z][A-Z0-9_$#]*$")]
    private static partial Regex SafeIdentifierRegex();

    public DbKind Kind => DbKind.Oracle;

    public string? RowAddressPseudoColumn => "ROWID";

    // ---------------------------------------------------------------- Подключение

    public string BuildConnectionString(DataSourceConfig config, string plainPassword)
    {
        var port = config.Port > 0 ? config.Port : 1521;
        // EZConnect; при включённом SSL — протокол tcps.
        var dataSource = config.UseSsl
            ? $"tcps://{config.Host}:{port}/{config.Database}"
            : $"{config.Host}:{port}/{config.Database}";

        var builder = new OracleConnectionStringBuilder
        {
            DataSource = dataSource,
            UserID = config.Username,
            Password = plainPassword,
            Pooling = true,
            ConnectionTimeout = config.ConnectTimeoutSeconds > 0 ? config.ConnectTimeoutSeconds : 15,
            MaxPoolSize = Math.Max(1, config.MaxPoolSizePerUser),
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
        var connection = new OracleConnection(BuildConnectionString(config, plainPassword));
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            if (config.ReadOnly)
            {
                // Read-only флаг датасорса: запрещаем запись на уровне транзакций сессии.
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = "ALTER SESSION SET READ ONLY"; // недопустимо в PDB read-write? тогда игнорируем ошибку
                try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                catch (OracleException) { /* не критично: read-only дополнительно проверяется на уровне приложения */ }
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
        try
        {
            await using var cmd = CreateCommand(connection, "SELECT banner FROM v$version WHERE ROWNUM = 1");
            var banner = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (banner is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        catch (OracleException)
        {
            // Нет прав на v$version — пробуем v$instance.
        }

        try
        {
            await using var cmd = CreateCommand(connection, "SELECT version FROM v$instance");
            var version = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (version is string s && !string.IsNullOrWhiteSpace(s))
                return $"Oracle {s}";
        }
        catch (OracleException)
        {
            // Нет прав и на v$instance — соединение при этом успешно открыто.
        }

        return "Oracle";
    }

    // ---------------------------------------------------------------- Интроспекция

    /// <summary>В Oracle подключение охватывает весь инстанс: отдельного уровня «базы данных» в дереве нет.</summary>
    public bool SupportsDatabaseLevel => false;

    public Task<IReadOnlyList<DbObjectNode>> GetDatabasesAsync(DbConnection connection, bool includeSystem, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DbObjectNode>>([]);

    public async Task<IReadOnlyList<string>> GetSchemasAsync(DbConnection connection, bool includeSystem, CancellationToken ct)
    {
        // В 12c+ фильтруем по ORACLE_MAINTAINED; на старых версиях — по статическому списку.
        if (!includeSystem)
        {
            try
            {
                return await QuerySchemaNamesAsync(connection,
                    "SELECT username FROM all_users WHERE oracle_maintained = 'N' ORDER BY username", ct).ConfigureAwait(false);
            }
            catch (OracleException)
            {
                var all = await QuerySchemaNamesAsync(connection,
                    "SELECT username FROM all_users ORDER BY username", ct).ConfigureAwait(false);
                return all.Where(u => !SystemSchemas.Contains(u)).ToList();
            }
        }

        return await QuerySchemaNamesAsync(connection,
            "SELECT username FROM all_users ORDER BY username", ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> QuerySchemaNamesAsync(DbConnection connection, string sql, CancellationToken ct)
    {
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
            {
                var schemas = await GetSchemasAsync(connection, includeSystem, ct).ConfigureAwait(false);
                return schemas
                    .Select(s => new DbObjectNode
                    {
                        Name = s,
                        Type = DbObjectType.Schema,
                        HasChildren = true,
                        IsSystem = SystemSchemas.Contains(s),
                    })
                    .ToList();
            }
            case 1:
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

    private static async Task<IReadOnlyList<DbObjectNode>> GetCategoryChildrenAsync(
        DbConnection connection, string owner, string category, CancellationToken ct)
    {
        // Линки БД в ALL_OBJECTS не попадают — у них собственный словарь.
        if (category == "DbLinks")
            return await GetDbLinkNodesAsync(connection, owner, ct).ConfigureAwait(false);

        (string objectType, DbObjectType nodeType, bool hasChildren) = category switch
        {
            "Tables" => ("TABLE", DbObjectType.Table, true),
            "Views" => ("VIEW", DbObjectType.View, true),
            "MaterializedViews" => ("MATERIALIZED VIEW", DbObjectType.MaterializedView, true),
            "Indexes" => ("INDEX", DbObjectType.Index, false),
            "Sequences" => ("SEQUENCE", DbObjectType.Sequence, false),
            "Synonyms" => ("SYNONYM", DbObjectType.Synonym, false),
            "Functions" => ("FUNCTION", DbObjectType.Function, false),
            "Procedures" => ("PROCEDURE", DbObjectType.Procedure, false),
            "Packages" => ("PACKAGE", DbObjectType.Package, false),
            "Types" => ("TYPE", DbObjectType.Type, false),
            "Triggers" => ("TRIGGER", DbObjectType.Trigger, false),
            _ => ("", DbObjectType.Table, false),
        };
        if (objectType.Length == 0)
            return [];

        // Обязательный фильтр по OWNER (:owner) — без него ALL_OBJECTS сканирует весь словарь.
        // Для TABLE исключаем таблицы-носители материализованных представлений и корзину (BIN$).
        var sql = objectType == "TABLE"
            ? """
              SELECT o.object_name FROM all_objects o
              WHERE o.owner = :owner AND o.object_type = 'TABLE'
                AND o.object_name NOT LIKE 'BIN$%'
                AND NOT EXISTS (SELECT 1 FROM all_mviews m WHERE m.owner = o.owner AND m.mview_name = o.object_name)
              ORDER BY o.object_name
              """
            : $"""
              SELECT o.object_name FROM all_objects o
              WHERE o.owner = :owner AND o.object_type = '{objectType}'
                AND o.object_name NOT LIKE 'BIN$%'
              ORDER BY o.object_name
              """;

        var result = new List<DbObjectNode>();
        await using var cmd = CreateCommand(connection, sql, ("owner", owner));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DbObjectNode
            {
                Name = reader.GetString(0),
                Type = nodeType,
                Schema = owner,
                HasChildren = hasChildren,
            });
        }
        return result;
    }

    /// <summary>Линки БД схемы: ALL_DB_LINKS (в ALL_OBJECTS этот тип отсутствует).</summary>
    private static async Task<IReadOnlyList<DbObjectNode>> GetDbLinkNodesAsync(
        DbConnection connection, string owner, CancellationToken ct)
    {
        var result = new List<DbObjectNode>();
        await using var cmd = CreateCommand(connection,
            "SELECT db_link, host FROM all_db_links WHERE owner = :owner ORDER BY db_link", ("owner", owner));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DbObjectNode
            {
                Name = reader.GetString(0),
                Type = DbObjectType.DbLink,
                Schema = owner,
                Comment = reader.IsDBNull(1) ? null : reader.GetString(1),
            });
        }
        return result;
    }

    private static async Task<IReadOnlyList<DbObjectNode>> GetColumnNodesAsync(
        DbConnection connection, string owner, string table, CancellationToken ct)
    {
        var sql = """
            SELECT column_name, data_type, data_length, data_precision, data_scale, nullable
            FROM all_tab_columns
            WHERE owner = :owner AND table_name = :table_name
            ORDER BY column_id
            """;

        var result = new List<DbObjectNode>();
        await using var cmd = CreateCommand(connection, sql, ("owner", owner), ("table_name", table));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var dataType = FormatDataType(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)),
                reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)));
            result.Add(new DbObjectNode
            {
                Name = reader.GetString(0),
                Type = DbObjectType.Column,
                Schema = owner,
                Attributes = new Dictionary<string, string>
                {
                    ["dataType"] = dataType,
                    ["nullable"] = reader.GetString(5) == "Y" ? "true" : "false",
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

    /// <summary>Загружает таблицы/представления схемы (OWNER = :owner) с колонками, PK и FK.</summary>
    private static async Task<IReadOnlyList<TableInfo>> LoadTablesAsync(
        DbConnection connection, string owner, string? tableFilter, CancellationToken ct)
    {
        var objectFilter = tableFilter is null ? "" : " AND o.object_name = :table_name";
        (string, object?)[] Params(string paramTableName = "table_name") => tableFilter is null
            ? [("owner", owner)]
            : [("owner", owner), (paramTableName, tableFilter)];

        // RESULT_CACHE: schema introspection repeats the same dictionary queries per editor session.
        // Oracle catalogs are slow, and the result set changes only on DDL.

        // 1. Объекты: таблицы, представления, материализованные представления
        //    (mview дублируется в ALL_OBJECTS как TABLE — дедупликация в пользу MATERIALIZED VIEW).
        var relations = new Dictionary<string, DbObjectType>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT /*+ RESULT_CACHE */ o.object_name, o.object_type
                FROM all_objects o
                WHERE o.owner = :owner
                  AND o.object_type IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW')
                  AND o.object_name NOT LIKE 'BIN$%'{objectFilter}
                ORDER BY o.object_name
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var type = reader.GetString(1) switch
                {
                    "VIEW" => DbObjectType.View,
                    "MATERIALIZED VIEW" => DbObjectType.MaterializedView,
                    _ => DbObjectType.Table,
                };
                if (type == DbObjectType.MaterializedView || !relations.ContainsKey(name))
                    relations[name] = type;
            }
        }

        // 2. Комментарии таблиц (ALL_TAB_COMMENTS, фильтр по OWNER).
        var tableComments = new Dictionary<string, string>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT /*+ RESULT_CACHE */ c.table_name, c.comments
                FROM all_tab_comments c
                WHERE c.owner = :owner AND c.comments IS NOT NULL{(tableFilter is null ? "" : " AND c.table_name = :table_name")}
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                tableComments[reader.GetString(0)] = reader.GetString(1);
        }

        // 3. Комментарии колонок (ALL_COL_COMMENTS, фильтр по OWNER).
        var columnComments = new Dictionary<(string Table, string Column), string>();
        {
            var sql = $"""
                SELECT /*+ RESULT_CACHE */ c.table_name, c.column_name, c.comments
                FROM all_col_comments c
                WHERE c.owner = :owner AND c.comments IS NOT NULL{(tableFilter is null ? "" : " AND c.table_name = :table_name")}
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                columnComments[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        // 4. Первичные ключи: ALL_CONSTRAINTS ('P') + ALL_CONS_COLUMNS, фильтр по OWNER.
        var pkByTable = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT /*+ RESULT_CACHE */ c.table_name, cc.column_name
                FROM all_constraints c
                JOIN all_cons_columns cc
                  ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name AND cc.table_name = c.table_name
                WHERE c.owner = :owner AND c.constraint_type = 'P' AND c.status = 'ENABLED'{(tableFilter is null ? "" : " AND c.table_name = :table_name")}
                ORDER BY c.table_name, cc.position
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var tableName = reader.GetString(0);
                if (!pkByTable.TryGetValue(tableName, out var list))
                    pkByTable[tableName] = list = [];
                list.Add(reader.GetString(1));
            }
        }

        // 5. Колонки: ALL_TAB_COLUMNS, фильтр по OWNER. DATA_DEFAULT — тип LONG (InitialLONGFetchSize = -1).
        var columnsByTable = new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT /*+ RESULT_CACHE */ c.table_name, c.column_name, c.data_type, c.data_length, c.data_precision, c.data_scale,
                       c.nullable, c.data_default, c.column_id
                FROM all_tab_columns c
                WHERE c.owner = :owner{(tableFilter is null ? "" : " AND c.table_name = :table_name")}
                ORDER BY c.table_name, c.column_id
                """;
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var tableName = reader.GetString(0);
                if (!relations.ContainsKey(tableName))
                    continue;
                var columnName = reader.GetString(1);
                var pk = pkByTable.TryGetValue(tableName, out var pkCols) && pkCols.Contains(columnName, StringComparer.Ordinal);
                if (!columnsByTable.TryGetValue(tableName, out var list))
                    columnsByTable[tableName] = list = [];
                list.Add(new ColumnInfo
                {
                    Name = columnName,
                    DataType = FormatDataType(
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                        reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                        reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5))),
                    IsNullable = reader.GetString(6) == "Y",
                    DefaultValue = reader.IsDBNull(7) ? null : reader.GetString(7)?.Trim(),
                    OrdinalPosition = reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8)),
                    IsPrimaryKey = pk,
                    Comment = columnComments.TryGetValue((tableName, columnName), out var comment) ? comment : null,
                });
            }
        }

        // 6. Внешние ключи: ALL_CONSTRAINTS ('R') + ALL_CONS_COLUMNS обеих сторон, фильтр по OWNER.
        var fkByTable = new Dictionary<string, List<ForeignKeyInfo>>(StringComparer.Ordinal);
        {
            var sql = $"""
                SELECT c.constraint_name, c.table_name, cc.column_name,
                       rc.owner AS r_owner, rc.table_name AS r_table, rcc.column_name AS r_column
                FROM all_constraints c
                JOIN all_cons_columns cc
                  ON cc.owner = c.owner AND cc.constraint_name = c.constraint_name AND cc.table_name = c.table_name
                JOIN all_constraints rc
                  ON rc.owner = c.r_owner AND rc.constraint_name = c.r_constraint_name
                JOIN all_cons_columns rcc
                  ON rcc.owner = rc.owner AND rcc.constraint_name = rc.constraint_name AND rcc.position = cc.position
                WHERE c.owner = :owner AND c.constraint_type = 'R'{(tableFilter is null ? "" : " AND c.table_name = :table_name")}
                ORDER BY c.table_name, c.constraint_name, cc.position
                """;
            var fkColumns = new Dictionary<(string Table, string Constraint), (string ROwner, string RTable, List<string> From, List<string> To)>();
            await using var cmd = CreateCommand(connection, sql, Params());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = (reader.GetString(1), reader.GetString(0));
                if (!fkColumns.TryGetValue(key, out var entry))
                    fkColumns[key] = entry = (reader.GetString(3), reader.GetString(4), [], []);
                entry.From.Add(reader.GetString(2));
                entry.To.Add(reader.GetString(5));
            }
            foreach (var ((tableName, constraintName), (rOwner, rTable, from, to)) in fkColumns)
            {
                if (!fkByTable.TryGetValue(tableName, out var list))
                    fkByTable[tableName] = list = [];
                list.Add(new ForeignKeyInfo
                {
                    ConstraintName = constraintName,
                    FromSchema = owner,
                    FromTable = tableName,
                    FromColumns = from,
                    ToSchema = rOwner,
                    ToTable = rTable,
                    ToColumns = to,
                });
            }
        }

        return relations
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new TableInfo
            {
                Schema = owner,
                Name = r.Key,
                Type = r.Value,
                Comment = tableComments.TryGetValue(r.Key, out var comment) ? comment : null,
                Columns = columnsByTable.TryGetValue(r.Key, out var cols) ? cols : [],
                PrimaryKeyColumns = pkByTable.TryGetValue(r.Key, out var pk) ? pk : [],
                ForeignKeys = fkByTable.TryGetValue(r.Key, out var fks) ? fks : [],
            })
            .ToList();
    }

    private static async Task<IReadOnlyList<RoutineInfo>> LoadRoutinesAsync(DbConnection connection, string owner, CancellationToken ct)
    {
        var sql = """
            SELECT o.object_name, o.object_type
            FROM all_objects o
            WHERE o.owner = :owner AND o.object_type IN ('FUNCTION', 'PROCEDURE')
            ORDER BY o.object_name
            """;

        var result = new List<RoutineInfo>();
        await using var cmd = CreateCommand(connection, sql, ("owner", owner));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RoutineInfo
            {
                Schema = owner,
                Name = reader.GetString(0),
                Type = reader.GetString(1) == "PROCEDURE" ? DbObjectType.Procedure : DbObjectType.Function,
            });
        }
        return result;
    }

    public async Task<string> GetSchemaVersionAsync(DbConnection connection, string schemaName, CancellationToken ct)
    {
        // Версия DDL-состояния схемы: количество объектов + максимальный LAST_DDL_TIME (обязательный фильтр OWNER).
        var sql = """
            SELECT COUNT(*), TO_CHAR(MAX(o.last_ddl_time), 'YYYYMMDDHH24MISS')
            FROM all_objects o
            WHERE o.owner = :owner
            """;

        await using var cmd = CreateCommand(connection, sql, ("owner", schemaName));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        long count = 0;
        var lastDdl = "";
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            count = Convert.ToInt64(reader.GetValue(0));
            lastDdl = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }
        return ComputeHash($"ora:{schemaName}:{count}:{lastDdl}");
    }

    // ---------------------------------------------------------------- Генерация SQL

    public string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        // Правило Oracle: неквотированные идентификаторы приводятся к UPPERCASE.
        // Без кавычек — только полностью верхний регистр вида ^[A-Z][A-Z0-9_$#]*$ и не зарезервированное слово.
        if (SafeIdentifierRegex().IsMatch(identifier) && !ReservedWords.Contains(identifier))
            return identifier;
        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    public string BuildSelectPageSql(TableInfo table, DataPageRequest request)
    {
        var hasPk = table.PrimaryKeyColumns.Count > 0;

        // Ключ keyset-пагинации: явный OrderBy (+PK как tiebreaker), иначе PK, иначе ROWID.
        List<string> keyColumns;
        if (!string.IsNullOrWhiteSpace(request.OrderBy))
        {
            keyColumns = [request.OrderBy!];
            keyColumns.AddRange(table.PrimaryKeyColumns.Where(c => !string.Equals(c, request.OrderBy, StringComparison.Ordinal)));
            if (!hasPk)
                keyColumns.Add("ROWID");
        }
        else
        {
            keyColumns = hasPk ? [.. table.PrimaryKeyColumns] : ["ROWID"];
        }

        var quotedKeys = keyColumns.Select(c => c == "ROWID" ? "ROWID" : QuoteIdentifier(c)).ToList();

        var sb = new StringBuilder("SELECT ");
        if (!hasPk)
            sb.Append("ROWID AS \"__ROWID\", "); // псевдоколонка адреса строки для таблиц без PK
        sb.Append(table.Columns.Count > 0
            ? string.Join(", ", table.Columns.Select(c => QuoteIdentifier(c.Name)))
            : "*");
        sb.Append(" FROM ").Append(QuoteIdentifier(table.Schema)).Append('.').Append(QuoteIdentifier(table.Name));

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.WhereFilter))
            where.Add($"({request.WhereFilter})");
        if (request.After is { Count: > 0 })
        {
            // Oracle не поддерживает кортежное сравнение (a,b) > (:x,:y) — разворачиваем в дизъюнкцию:
            // (k1 > :a0) OR (k1 = :a0 AND k2 > :a1) OR ...
            var op = request.OrderDescending ? "<" : ">";
            var branches = new List<string>();
            for (var i = 0; i < quotedKeys.Count; i++)
            {
                var parts = new List<string>();
                for (var j = 0; j < i; j++)
                    parts.Add($"{quotedKeys[j]} = :after{j}");
                parts.Add($"{quotedKeys[i]} {op} :after{i}");
                branches.Add("(" + string.Join(" AND ", parts) + ")");
            }
            where.Add("(" + string.Join(" OR ", branches) + ")");
        }
        if (where.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", where));

        var direction = request.OrderDescending ? " DESC" : "";
        sb.Append(" ORDER BY ").Append(string.Join(", ", quotedKeys.Select(k => k + direction)));
        sb.Append(" FETCH FIRST ").Append(Math.Clamp(request.Limit, 1, 10_000)).Append(" ROWS ONLY");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>Форматирует тип данных Oracle: NUMBER(p,s), VARCHAR2(n) и т.п.</summary>
    private static string FormatDataType(string dataType, int? length, int? precision, int? scale)
    {
        switch (dataType)
        {
            case "NUMBER":
                if (precision is null)
                    return "NUMBER";
                return scale is > 0 ? $"NUMBER({precision},{scale})" : $"NUMBER({precision})";
            case "VARCHAR2":
            case "NVARCHAR2":
            case "CHAR":
            case "NCHAR":
            case "RAW":
                return length is > 0 ? $"{dataType}({length})" : dataType;
            default:
                return dataType;
        }
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        if (cmd is OracleCommand oracleCmd)
        {
            oracleCmd.BindByName = true;          // именованные параметры :name
            oracleCmd.InitialLONGFetchSize = -1;  // чтение LONG-колонок (DATA_DEFAULT) целиком
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

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }
}
