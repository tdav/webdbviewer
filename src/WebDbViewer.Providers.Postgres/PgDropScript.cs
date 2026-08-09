using System.Data.Common;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;

namespace WebDbViewer.Providers.Postgres;

/// <summary>
/// Скрипты удаления объектов PostgreSQL.
/// Текст собирается запросом к каталогу, а не склейкой строк в C#, по двум причинам:
/// объект попутно проверяется на существование, и оттуда же берутся сигнатуры перегрузок
/// (для функций, агрегатов и операторов имя без сигнатуры не определяет объект).
/// CASCADE не добавляется: каскад должен дописать человек, увидев скрипт.
/// </summary>
internal static class PgDropScript
{
    public static async Task<string> BuildAsync(
        DbConnection connection, string schema, string name, DbObjectType type, string? qualifier, CancellationToken ct)
    {
        var (sql, usesQualifier) = type switch
        {
            DbObjectType.Table => (RelationSql("TABLE", "('r','p')"), false),
            DbObjectType.ForeignTable => (RelationSql("FOREIGN TABLE", "('f')"), false),
            DbObjectType.View => (RelationSql("VIEW", "('v')"), false),
            DbObjectType.MaterializedView => (RelationSql("MATERIALIZED VIEW", "('m')"), false),
            DbObjectType.Index => (RelationSql("INDEX", "('i','I')"), false),
            DbObjectType.Sequence => (RelationSql("SEQUENCE", "('S')"), false),
            DbObjectType.Function => (RoutineSql("FUNCTION", 'f'), true),
            DbObjectType.Procedure => (RoutineSql("PROCEDURE", 'p'), true),
            DbObjectType.Aggregate => (RoutineSql("AGGREGATE", 'a'), true),
            DbObjectType.Type => (TypeSql("TYPE", "'e','c','r'"), false),
            DbObjectType.Domain => (TypeSql("DOMAIN", "'d'"), false),
            DbObjectType.Operator => (OperatorSql, false),
            DbObjectType.Collation => (CollationSql, false),
            DbObjectType.TextSearchConfig => (TextSearchSql("CONFIGURATION", "pg_ts_config", "cfgname", "cfgnamespace"), false),
            DbObjectType.TextSearchDictionary => (TextSearchSql("DICTIONARY", "pg_ts_dict", "dictname", "dictnamespace"), false),
            DbObjectType.Trigger => (TriggerSql, true),
            DbObjectType.Rule => (RuleSql, true),
            DbObjectType.Policy => (PolicySql, true),
            _ => throw new NotSupportedException($"Скрипт удаления объектов типа «{type}» не поддерживается."),
        };

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "schema", schema);
        AddParameter(cmd, "name", name);
        if (usesQualifier)
            AddParameter(cmd, "qualifier", (object?)qualifier ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull)
            throw new DdlObjectNotFoundException($"Объект «{schema}.{name}» типа «{type}» не найден.");

        return ((string)result).TrimEnd() + "\n";
    }

    /// <summary>
    /// Тип задаётся явно: для NULL-значения сервер иначе не может вывести тип параметра
    /// в условии вида «@qualifier IS NULL» и отказывается готовить запрос.
    /// </summary>
    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = System.Data.DbType.String;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    // ------------------------------------------------------------------ Отношения

    /// <summary>relkindList — константа из кода, не пользовательский ввод.</summary>
    private static string RelationSql(string keyword, string relkindList) => $"""
        SELECT format('DROP {keyword} %s;', c.oid::regclass::text)
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relname = @name AND c.relkind IN {relkindList}
        """;

    // ------------------------------------------------------------------ Функции, процедуры, агрегаты

    /// <summary>
    /// Без сигнатуры (@qualifier IS NULL) скрипт содержит удаление ВСЕХ перегрузок — каждое
    /// на своей строке. Так пользователь видит, что имя неоднозначно, и убирает лишнее сам.
    /// </summary>
    private static string RoutineSql(string keyword, char prokind) => $"""
        SELECT string_agg(
                   format('DROP {keyword} %I.%I(%s);', n.nspname, p.proname,
                          pg_catalog.pg_get_function_identity_arguments(p.oid)),
                   E'\n' ORDER BY p.oid)
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema AND p.proname = @name AND p.prokind = '{prokind}'
          AND (@qualifier IS NULL OR pg_catalog.pg_get_function_identity_arguments(p.oid) = @qualifier)
        """;

    // ------------------------------------------------------------------ Типы и домены

    private static string TypeSql(string keyword, string typtypeList) => $"""
        SELECT format('DROP {keyword} %s;', t.oid::regtype::text)
        FROM pg_catalog.pg_type t
        JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname = @schema AND t.typname = @name AND t.typtype IN ({typtypeList})
          AND (t.typrelid = 0 OR (SELECT c.relkind FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid) = 'c')
        """;

    // ------------------------------------------------------------------ Оператор

    private const string OperatorSql = """
        SELECT format('DROP OPERATOR %I.%s (%s, %s);', n.nspname, o.oprname,
                   CASE WHEN o.oprleft  = 0 THEN 'NONE' ELSE pg_catalog.format_type(o.oprleft,  NULL) END,
                   CASE WHEN o.oprright = 0 THEN 'NONE' ELSE pg_catalog.format_type(o.oprright, NULL) END)
        FROM pg_catalog.pg_operator o
        JOIN pg_catalog.pg_namespace n ON n.oid = o.oprnamespace
        WHERE n.nspname = @schema AND o.oprname = @name
        """;

    // ------------------------------------------------------------------ Правило сортировки

    private const string CollationSql = """
        SELECT format('DROP COLLATION %I.%I;', n.nspname, cl.collname)
        FROM pg_catalog.pg_collation cl
        JOIN pg_catalog.pg_namespace n ON n.oid = cl.collnamespace
        WHERE n.nspname = @schema AND cl.collname = @name
        """;

    // ------------------------------------------------------------------ Полнотекстовый поиск

    private static string TextSearchSql(string keyword, string catalog, string nameColumn, string namespaceColumn) => $"""
        SELECT format('DROP TEXT SEARCH {keyword} %I.%I;', n.nspname, o.{nameColumn})
        FROM pg_catalog.{catalog} o
        JOIN pg_catalog.pg_namespace n ON n.oid = o.{namespaceColumn}
        WHERE n.nspname = @schema AND o.{nameColumn} = @name
        """;

    // ------------------------------------------------------------------ Объекты, принадлежащие таблице

    private const string TriggerSql = """
        SELECT format('DROP TRIGGER %I ON %s;', t.tgname, c.oid::regclass::text)
        FROM pg_catalog.pg_trigger t
        JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND t.tgname = @name AND NOT t.tgisinternal
          AND (@qualifier IS NULL OR c.relname = @qualifier)
        """;

    private const string RuleSql = """
        SELECT format('DROP RULE %I ON %s;', r.rulename, c.oid::regclass::text)
        FROM pg_catalog.pg_rewrite r
        JOIN pg_catalog.pg_class c ON c.oid = r.ev_class
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND r.rulename = @name AND r.rulename <> '_RETURN'
          AND (@qualifier IS NULL OR c.relname = @qualifier)
        """;

    private const string PolicySql = """
        SELECT format('DROP POLICY %I ON %s;', pl.polname, c.oid::regclass::text)
        FROM pg_catalog.pg_policy pl
        JOIN pg_catalog.pg_class c ON c.oid = pl.polrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND pl.polname = @name
          AND (@qualifier IS NULL OR c.relname = @qualifier)
        """;
}
