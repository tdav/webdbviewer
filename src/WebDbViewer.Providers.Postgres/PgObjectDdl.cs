using System.Data.Common;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;

namespace WebDbViewer.Providers.Postgres;

/// <summary>
/// DDL объектов PostgreSQL, для которых нет готовой функции вида pg_get_*def:
/// последовательности, типы, домены, агрегаты, операторы, правила сортировки,
/// объекты полнотекстового поиска, внешние таблицы, политики RLS.
/// Текст собирается запросом к каталогу через format(): так экранирование
/// идентификаторов и литералов делает сам сервер.
/// </summary>
internal static class PgObjectDdl
{
    public static async Task<string> BuildAsync(
        DbConnection connection, string schema, string name, DbObjectType type, string? owner, CancellationToken ct)
    {
        var (sql, needsOwner) = type switch
        {
            DbObjectType.Sequence => (SequenceSql, false),
            DbObjectType.Type => (TypeSql, false),
            DbObjectType.Domain => (DomainSql, false),
            DbObjectType.ForeignTable => (ForeignTableSql, false),
            DbObjectType.Aggregate => (AggregateSql, false),
            DbObjectType.Operator => (OperatorSql, false),
            DbObjectType.Collation => (CollationSql(connection), false),
            DbObjectType.TextSearchConfig => (TextSearchConfigSql, false),
            DbObjectType.TextSearchDictionary => (TextSearchDictionarySql, false),
            DbObjectType.Trigger => (TriggerSql, true),
            DbObjectType.Rule => (RuleSql, true),
            DbObjectType.Policy => (PolicySql, true),
            _ => throw new NotSupportedException($"DDL объектов типа «{type}» не поддерживается."),
        };

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        AddParameter(cmd, "schema", schema);
        AddParameter(cmd, "name", name);
        if (needsOwner)
            AddParameter(cmd, "owner", (object?)owner ?? DBNull.Value);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            var where = owner is { Length: > 0 } ? $"{schema}.{owner}" : schema;
            throw new DdlObjectNotFoundException($"Объект «{name}» типа «{type}» не найден в «{where}».");
        }

        return ((string)result).TrimEnd() + "\n";
    }

    /// <summary>
    /// Тип задаётся явно: без него сервер не может вывести тип NULL-параметра
    /// в условии «@owner IS NULL» и отказывается готовить запрос.
    /// </summary>
    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = System.Data.DbType.String;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    // ------------------------------------------------------------------ Последовательность

    private const string SequenceSql = """
        SELECT format(
                   E'CREATE SEQUENCE %s\n    AS %s\n    INCREMENT BY %s\n    MINVALUE %s\n    MAXVALUE %s\n    START WITH %s\n    CACHE %s%s;%s',
                   s.seqrelid::regclass::text,
                   pg_catalog.format_type(s.seqtypid, NULL),
                   s.seqincrement, s.seqmin, s.seqmax, s.seqstart, s.seqcache,
                   CASE WHEN s.seqcycle THEN E'\n    CYCLE' ELSE '' END,
                   coalesce(
                       (SELECT format(E'\n\nALTER SEQUENCE %s OWNED BY %s.%I;',
                                      s.seqrelid::regclass::text, d.refobjid::regclass::text, a.attname)
                          FROM pg_catalog.pg_depend d
                          JOIN pg_catalog.pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid
                         WHERE d.objid = s.seqrelid AND d.classid = 'pg_class'::regclass AND d.deptype = 'a'),
                       ''))
        FROM pg_catalog.pg_sequence s
        JOIN pg_catalog.pg_class c ON c.oid = s.seqrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND c.relname = @name
        """;

    // ------------------------------------------------------------------ Типы: enum / составной / диапазон

    private const string TypeSql = """
        SELECT CASE t.typtype
            WHEN 'e' THEN format('CREATE TYPE %s AS ENUM (%s);',
                     t.oid::regtype::text,
                     (SELECT string_agg(quote_literal(e.enumlabel), ', ' ORDER BY e.enumsortorder)
                        FROM pg_catalog.pg_enum e WHERE e.enumtypid = t.oid))
            WHEN 'c' THEN format(E'CREATE TYPE %s AS (\n%s\n);',
                     t.oid::regtype::text,
                     (SELECT string_agg(format('    %I %s', a.attname,
                                               pg_catalog.format_type(a.atttypid, a.atttypmod)), E',\n' ORDER BY a.attnum)
                        FROM pg_catalog.pg_attribute a
                       WHERE a.attrelid = t.typrelid AND a.attnum > 0 AND NOT a.attisdropped))
            WHEN 'r' THEN format(E'CREATE TYPE %s AS RANGE (\n    subtype = %s%s%s\n);',
                     t.oid::regtype::text,
                     pg_catalog.format_type(r.rngsubtype, NULL),
                     CASE WHEN r.rngcanonical <> 0 THEN E',\n    canonical = ' || r.rngcanonical::regproc::text ELSE '' END,
                     CASE WHEN r.rngsubdiff  <> 0 THEN E',\n    subtype_diff = ' || r.rngsubdiff::regproc::text ELSE '' END)
        END
        FROM pg_catalog.pg_type t
        JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
        LEFT JOIN pg_catalog.pg_range r ON r.rngtypid = t.oid
        WHERE n.nspname = @schema AND t.typname = @name AND t.typtype IN ('e','c','r')
          AND (t.typrelid = 0 OR (SELECT c.relkind FROM pg_catalog.pg_class c WHERE c.oid = t.typrelid) = 'c')
        """;

    // ------------------------------------------------------------------ Домен

    private const string DomainSql = """
        SELECT format('CREATE DOMAIN %s AS %s%s%s%s;',
                   t.oid::regtype::text,
                   pg_catalog.format_type(t.typbasetype, t.typtypmod),
                   CASE WHEN t.typnotnull THEN E'\n    NOT NULL' ELSE '' END,
                   CASE WHEN t.typdefault IS NOT NULL THEN E'\n    DEFAULT ' || t.typdefault ELSE '' END,
                   coalesce((SELECT string_agg(format(E'\n    CONSTRAINT %I %s', c.conname,
                                                      pg_catalog.pg_get_constraintdef(c.oid, true)), '')
                               FROM pg_catalog.pg_constraint c WHERE c.contypid = t.oid), ''))
        FROM pg_catalog.pg_type t
        JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace
        WHERE n.nspname = @schema AND t.typname = @name AND t.typtype = 'd'
        """;

    // ------------------------------------------------------------------ Внешняя таблица

    private const string ForeignTableSql = """
        SELECT format(E'CREATE FOREIGN TABLE %I.%I (\n%s\n)\nSERVER %I%s;',
                   n.nspname, c.relname,
                   (SELECT string_agg(format('    %I %s%s', a.attname,
                                             pg_catalog.format_type(a.atttypid, a.atttypmod),
                                             CASE WHEN a.attnotnull THEN ' NOT NULL' ELSE '' END), E',\n' ORDER BY a.attnum)
                      FROM pg_catalog.pg_attribute a
                     WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped),
                   s.srvname,
                   CASE WHEN coalesce(array_length(ft.ftoptions, 1), 0) > 0
                        THEN E'\nOPTIONS (' || (SELECT string_agg(format('%I %L', split_part(o, '=', 1),
                                                                        substr(o, strpos(o, '=') + 1)), ', ')
                                                  FROM unnest(ft.ftoptions) AS o) || ')'
                        ELSE '' END)
        FROM pg_catalog.pg_foreign_table ft
        JOIN pg_catalog.pg_class c ON c.oid = ft.ftrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_catalog.pg_foreign_server s ON s.oid = ft.ftserver
        WHERE n.nspname = @schema AND c.relname = @name
        """;

    // ------------------------------------------------------------------ Агрегатная функция

    private const string AggregateSql = """
        SELECT format(E'CREATE AGGREGATE %I.%I(%s) (\n    SFUNC = %s,\n    STYPE = %s%s%s%s\n);',
                   n.nspname, p.proname,
                   pg_catalog.pg_get_function_identity_arguments(p.oid),
                   a.aggtransfn::regproc::text,
                   pg_catalog.format_type(a.aggtranstype, NULL),
                   CASE WHEN a.agginitval IS NOT NULL THEN E',\n    INITCOND = ' || quote_literal(a.agginitval) ELSE '' END,
                   CASE WHEN a.aggfinalfn <> 0 THEN E',\n    FINALFUNC = ' || a.aggfinalfn::regproc::text ELSE '' END,
                   CASE WHEN a.aggsortop  <> 0 THEN E',\n    SORTOP = ' || a.aggsortop::regoperator::text ELSE '' END)
        FROM pg_catalog.pg_aggregate a
        JOIN pg_catalog.pg_proc p ON p.oid = a.aggfnoid
        JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = @schema AND p.proname = @name
        """;

    // ------------------------------------------------------------------ Оператор

    private const string OperatorSql = """
        SELECT format(E'CREATE OPERATOR %I.%s (\n    FUNCTION = %s%s%s%s%s\n);',
                   n.nspname, o.oprname,
                   o.oprcode::regproc::text,
                   CASE WHEN o.oprleft  <> 0 THEN E',\n    LEFTARG = '  || pg_catalog.format_type(o.oprleft, NULL)  ELSE '' END,
                   CASE WHEN o.oprright <> 0 THEN E',\n    RIGHTARG = ' || pg_catalog.format_type(o.oprright, NULL) ELSE '' END,
                   CASE WHEN o.oprcom    <> 0 THEN E',\n    COMMUTATOR = OPERATOR(' || (SELECT quote_ident(n2.nspname) || '.' || o2.oprname
                                                                                          FROM pg_catalog.pg_operator o2
                                                                                          JOIN pg_catalog.pg_namespace n2 ON n2.oid = o2.oprnamespace
                                                                                         WHERE o2.oid = o.oprcom) || ')' ELSE '' END,
                   CASE WHEN o.oprnegate <> 0 THEN E',\n    NEGATOR = OPERATOR(' || (SELECT quote_ident(n3.nspname) || '.' || o3.oprname
                                                                                       FROM pg_catalog.pg_operator o3
                                                                                       JOIN pg_catalog.pg_namespace n3 ON n3.oid = o3.oprnamespace
                                                                                      WHERE o3.oid = o.oprnegate) || ')' ELSE '' END)
        FROM pg_catalog.pg_operator o
        JOIN pg_catalog.pg_namespace n ON n.oid = o.oprnamespace
        WHERE n.nspname = @schema AND o.oprname = @name
        """;

    // ------------------------------------------------------------------ Правило сортировки

    /// <summary>
    /// Колонка с локалью ICU меняла имя между версиями: colliculocale (15–16) → colllocale (17+).
    /// Запрос собирается под версию сервера, иначе на «чужой» версии он просто не скомпилируется.
    /// </summary>
    private static string CollationSql(DbConnection connection)
    {
        var localeColumn = MajorVersion(connection) switch
        {
            >= 17 => "cl.colllocale",
            >= 15 => "cl.colliculocale",
            _ => "NULL::text",
        };

        return $$"""
            SELECT format(E'CREATE COLLATION %I.%I (\n    provider = %s,\n%s%s\n);',
                       n.nspname, cl.collname,
                       CASE cl.collprovider WHEN 'i' THEN 'icu' WHEN 'b' THEN 'builtin' ELSE 'libc' END,
                       CASE WHEN cl.collprovider IN ('i','b')
                            THEN '    locale = ' || quote_literal(coalesce({{localeColumn}}, cl.collcollate))
                            ELSE '    lc_collate = ' || quote_literal(cl.collcollate) ||
                                 E',\n    lc_ctype = ' || quote_literal(cl.collctype) END,
                       CASE WHEN NOT cl.collisdeterministic THEN E',\n    deterministic = false' ELSE '' END)
            FROM pg_catalog.pg_collation cl
            JOIN pg_catalog.pg_namespace n ON n.oid = cl.collnamespace
            WHERE n.nspname = @schema AND cl.collname = @name
            """;
    }

    private static int MajorVersion(DbConnection connection)
    {
        var version = connection.ServerVersion ?? "";
        var dot = version.IndexOf('.');
        var major = dot > 0 ? version[..dot] : version;
        return int.TryParse(major, out var value) ? value : 0;
    }

    // ------------------------------------------------------------------ Полнотекстовый поиск

    private const string TextSearchConfigSql = """
        SELECT format('CREATE TEXT SEARCH CONFIGURATION %I.%I (PARSER = %s);%s',
                   n.nspname, tc.cfgname,
                   (SELECT quote_ident(pn.nspname) || '.' || quote_ident(p.prsname)
                      FROM pg_catalog.pg_ts_parser p
                      JOIN pg_catalog.pg_namespace pn ON pn.oid = p.prsnamespace
                     WHERE p.oid = tc.cfgparser),
                   coalesce((SELECT string_agg(
                                 format(E'\n\nALTER TEXT SEARCH CONFIGURATION %I.%I\n    ADD MAPPING FOR %s WITH %s;',
                                        n.nspname, tc.cfgname, tt.alias, m.dicts), '' ORDER BY tt.alias)
                               FROM (SELECT m.maptokentype,
                                            string_agg(quote_ident(dn.nspname) || '.' || quote_ident(d.dictname), ', '
                                                       ORDER BY m.mapseqno) AS dicts
                                       FROM pg_catalog.pg_ts_config_map m
                                       JOIN pg_catalog.pg_ts_dict d ON d.oid = m.mapdict
                                       JOIN pg_catalog.pg_namespace dn ON dn.oid = d.dictnamespace
                                      WHERE m.mapcfg = tc.oid
                                      GROUP BY m.maptokentype) m
                               JOIN LATERAL pg_catalog.ts_token_type(tc.cfgparser) tt ON tt.tokid = m.maptokentype), ''))
        FROM pg_catalog.pg_ts_config tc
        JOIN pg_catalog.pg_namespace n ON n.oid = tc.cfgnamespace
        WHERE n.nspname = @schema AND tc.cfgname = @name
        """;

    private const string TextSearchDictionarySql = """
        SELECT format(E'CREATE TEXT SEARCH DICTIONARY %I.%I (\n    TEMPLATE = %s%s\n);',
                   n.nspname, td.dictname,
                   (SELECT quote_ident(tn.nspname) || '.' || quote_ident(t.tmplname)
                      FROM pg_catalog.pg_ts_template t
                      JOIN pg_catalog.pg_namespace tn ON tn.oid = t.tmplnamespace
                     WHERE t.oid = td.dicttemplate),
                   CASE WHEN td.dictinitoption IS NOT NULL THEN E',\n    ' || td.dictinitoption ELSE '' END)
        FROM pg_catalog.pg_ts_dict td
        JOIN pg_catalog.pg_namespace n ON n.oid = td.dictnamespace
        WHERE n.nspname = @schema AND td.dictname = @name
        """;

    // ------------------------------------------------------------------ Объекты, принадлежащие таблице

    private const string TriggerSql = """
        SELECT pg_catalog.pg_get_triggerdef(t.oid, true) || ';'
        FROM pg_catalog.pg_trigger t
        JOIN pg_catalog.pg_class c ON c.oid = t.tgrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND t.tgname = @name AND NOT t.tgisinternal
          AND (@owner IS NULL OR c.relname = @owner)
        """;

    private const string RuleSql = """
        SELECT pg_catalog.pg_get_ruledef(r.oid, true)
        FROM pg_catalog.pg_rewrite r
        JOIN pg_catalog.pg_class c ON c.oid = r.ev_class
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND r.rulename = @name AND r.rulename <> '_RETURN'
          AND (@owner IS NULL OR c.relname = @owner)
        """;

    private const string PolicySql = """
        SELECT format('CREATE POLICY %I ON %s%s FOR %s TO %s%s%s;',
                   pl.polname, pl.polrelid::regclass::text,
                   CASE WHEN pl.polpermissive THEN '' ELSE E'\n    AS RESTRICTIVE' END,
                   CASE pl.polcmd WHEN 'r' THEN 'SELECT' WHEN 'a' THEN 'INSERT'
                                  WHEN 'w' THEN 'UPDATE' WHEN 'd' THEN 'DELETE' ELSE 'ALL' END,
                   coalesce((SELECT string_agg(quote_ident(ro.rolname), ', ' ORDER BY ro.rolname)
                               FROM pg_catalog.pg_roles ro WHERE ro.oid = ANY(pl.polroles)), 'PUBLIC'),
                   CASE WHEN pl.polqual IS NOT NULL
                        THEN E'\n    USING (' || pg_catalog.pg_get_expr(pl.polqual, pl.polrelid) || ')' ELSE '' END,
                   CASE WHEN pl.polwithcheck IS NOT NULL
                        THEN E'\n    WITH CHECK (' || pg_catalog.pg_get_expr(pl.polwithcheck, pl.polrelid) || ')' ELSE '' END)
        FROM pg_catalog.pg_policy pl
        JOIN pg_catalog.pg_class c ON c.oid = pl.polrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = @schema AND pl.polname = @name
          AND (@owner IS NULL OR c.relname = @owner)
        """;
}
