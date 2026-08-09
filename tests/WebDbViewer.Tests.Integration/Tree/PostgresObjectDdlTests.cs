using Npgsql;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;
using WebDbViewer.Providers.Postgres;

namespace WebDbViewer.Tests.Integration.Tree;

/// <summary>
/// Генерация DDL объектов на демонстрационной базе (tools/testdata/pg_demo.sql).
/// Проверяются как ключевые фрагменты текста, так и его исполнимость: DDL
/// применяется поверх удалённого объекта внутри транзакции, которая откатывается.
/// </summary>
public sealed class PostgresObjectDdlTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=webdbviewer_demo;Username=postgres;Password=1;Pooling=true;";

    private readonly string connectionString =
        Environment.GetEnvironmentVariable("WEBDBVIEWER_TEST_DEMO_DB") ?? DefaultConnectionString;

    private readonly PgDdlGenerator generator = new();
    private NpgsqlConnection? connection;
    private bool available;

    public async Task InitializeAsync()
    {
        try
        {
            connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            await using var probe = new NpgsqlCommand(
                "SELECT count(*) FROM pg_namespace WHERE nspname = 'demo_core'", connection);
            available = Convert.ToInt64(await probe.ExecuteScalarAsync()) == 1;
        }
        catch (Exception)
        {
            available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
    }

    // Тип объекта, схема, имя, таблица-владелец, обязательные фрагменты DDL.
    public static TheoryData<DbObjectType, string, string, string?, string[]> Objects() => new()
    {
        { DbObjectType.Sequence, "demo_core", "invoice_number_seq", null,
          ["CREATE SEQUENCE", "INCREMENT BY 5", "START WITH 1000", "CYCLE"] },
        { DbObjectType.Sequence, "demo_core", "products_id_seq", null,
          ["CREATE SEQUENCE", "OWNED BY"] },
        { DbObjectType.Type, "demo_core", "order_status", null,
          ["CREATE TYPE", "AS ENUM", "'shipped'"] },
        { DbObjectType.Type, "demo_core", "address", null,
          ["CREATE TYPE", "country text", "zip text"] },
        { DbObjectType.Type, "demo_core", "money_range", null,
          ["CREATE TYPE", "AS RANGE", "subtype = numeric"] },
        { DbObjectType.Domain, "demo_core", "email", null,
          ["CREATE DOMAIN", "AS text", "CHECK"] },
        { DbObjectType.Domain, "demo_core", "positive_amount", null,
          ["CREATE DOMAIN", "numeric(18,2)", "NOT NULL", "DEFAULT"] },
        { DbObjectType.ForeignTable, "demo_extra", "customers_remote", null,
          ["CREATE FOREIGN TABLE", "SERVER demo_fdw_server", "OPTIONS", "table_name"] },
        { DbObjectType.Aggregate, "demo_core", "sum_positive", null,
          ["CREATE AGGREGATE", "SFUNC", "STYPE = numeric", "INITCOND"] },
        { DbObjectType.Operator, "demo_extra", "=~=", null,
          ["CREATE OPERATOR", "FUNCTION", "LEFTARG = numeric", "COMMUTATOR"] },
        { DbObjectType.Collation, "demo_extra", "demo_c", null,
          ["CREATE COLLATION"] },
        { DbObjectType.TextSearchConfig, "demo_extra", "demo_tsconfig", null,
          ["CREATE TEXT SEARCH CONFIGURATION", "PARSER", "ADD MAPPING FOR"] },
        { DbObjectType.TextSearchDictionary, "demo_extra", "demo_dict", null,
          ["CREATE TEXT SEARCH DICTIONARY", "TEMPLATE"] },
        { DbObjectType.Trigger, "demo_core", "tg_products_search", "products",
          ["CREATE TRIGGER", "BEFORE INSERT OR UPDATE", "EXECUTE FUNCTION"] },
        { DbObjectType.Trigger, "demo_core", "tg_orders_total_check", "orders",
          ["CREATE CONSTRAINT TRIGGER", "DEFERRABLE INITIALLY DEFERRED"] },
        { DbObjectType.Rule, "demo_core", "r_no_delete_settings", "settings",
          ["CREATE RULE", "ON DELETE", "DO INSTEAD NOTHING"] },
        { DbObjectType.Policy, "demo_extra", "p_secured_docs_own", "secured_docs",
          ["CREATE POLICY", "FOR SELECT", "demo_reader", "USING"] },
        { DbObjectType.Policy, "demo_extra", "p_secured_docs_write", "secured_docs",
          ["CREATE POLICY", "FOR ALL", "WITH CHECK"] },
    };

    [SkippableTheory]
    [MemberData(nameof(Objects))]
    public async Task DDL_объекта_содержит_ключевые_фрагменты(
        DbObjectType type, string schema, string name, string? owner, string[] fragments)
    {
        Skip.IfNot(available);

        var ddl = await generator.GetObjectDdlAsync(connection!, schema, name, type, owner, CancellationToken.None);

        Assert.All(fragments, fragment => Assert.Contains(fragment, ddl, StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Несуществующий_объект_даёт_понятную_ошибку()
    {
        Skip.IfNot(available);

        await Assert.ThrowsAsync<DdlObjectNotFoundException>(() =>
            generator.GetObjectDdlAsync(connection!, "demo_core", "нет_такого", DbObjectType.Domain, null, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Объект_таблицы_находится_без_указания_владельца()
    {
        Skip.IfNot(available);

        // Владелец не задан: параметр уходит как NULL, и запрос обязан это пережить.
        var ddl = await generator.GetObjectDdlAsync(
            connection!, "demo_core", "tg_products_search", DbObjectType.Trigger, null, CancellationToken.None);

        Assert.Contains("ON demo_core.products", ddl, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Одноимённые_триггеры_различаются_по_таблице_владельцу()
    {
        Skip.IfNot(available);

        // tg_customers_stmt_audit и tg_orders_big_audit используют одну функцию, но разные таблицы.
        var customers = await generator.GetObjectDdlAsync(
            connection!, "demo_core", "tg_customers_stmt_audit", DbObjectType.Trigger, "customers", CancellationToken.None);
        var orders = await generator.GetObjectDdlAsync(
            connection!, "demo_core", "tg_orders_big_audit", DbObjectType.Trigger, "orders", CancellationToken.None);

        Assert.Contains("ON demo_core.customers", customers, StringComparison.Ordinal);
        Assert.Contains("FOR EACH STATEMENT", customers, StringComparison.Ordinal);
        Assert.Contains("ON demo_core.orders", orders, StringComparison.Ordinal);
        Assert.Contains("WHEN", orders, StringComparison.Ordinal);
    }

    // Объекты, которые можно снести и пересоздать внутри транзакции без каскадных зависимостей.
    public static TheoryData<DbObjectType, string, string, string?> Executable() => new()
    {
        { DbObjectType.Trigger, "demo_core", "tg_products_search", "products" },
        { DbObjectType.Policy, "demo_extra", "p_secured_docs_own", "secured_docs" },
        { DbObjectType.Rule, "demo_core", "r_no_delete_settings", "settings" },
        { DbObjectType.Operator, "demo_extra", "=~=", null },
        { DbObjectType.Collation, "demo_extra", "demo_c", null },
        { DbObjectType.TextSearchConfig, "demo_extra", "demo_tsconfig", null },
        { DbObjectType.Aggregate, "demo_core", "sum_positive", null },
    };

    /// <summary>
    /// Пара «скрипт удаления + DDL» должна замыкаться: сгенерированный DROP сносит объект,
    /// сгенерированный CREATE возвращает его. Всё внутри транзакции, которая откатывается,
    /// поэтому демонстрационная база остаётся нетронутой.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Executable))]
    public async Task Скрипт_удаления_и_DDL_замыкаются(
        DbObjectType type, string schema, string name, string? qualifier)
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;
        var ddl = await generator.GetObjectDdlAsync(connection!, schema, name, type, qualifier, ct);
        var drop = await generator.GetDropScriptAsync(connection!, schema, name, type, qualifier, ct);

        Assert.StartsWith("DROP ", drop, StringComparison.Ordinal);
        Assert.DoesNotContain("CASCADE", drop, StringComparison.OrdinalIgnoreCase);

        await using var tx = await connection!.BeginTransactionAsync(ct);
        try
        {
            await using (var dropCmd = new NpgsqlCommand(drop, connection, (NpgsqlTransaction)tx))
                await dropCmd.ExecuteNonQueryAsync(ct);

            await using (var create = new NpgsqlCommand(ddl, connection, (NpgsqlTransaction)tx))
                await create.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await tx.RollbackAsync(ct);
        }
    }

    [SkippableTheory]
    [InlineData(DbObjectType.Table, "demo_core", "customers", "DROP TABLE demo_core.customers;")]
    [InlineData(DbObjectType.View, "demo_core", "v_active_customers", "DROP VIEW demo_core.v_active_customers;")]
    [InlineData(DbObjectType.MaterializedView, "demo_core", "mv_product_sales", "DROP MATERIALIZED VIEW demo_core.mv_product_sales;")]
    [InlineData(DbObjectType.Index, "demo_core", "ix_customers_last_name", "DROP INDEX demo_core.ix_customers_last_name;")]
    [InlineData(DbObjectType.Sequence, "demo_core", "invoice_number_seq", "DROP SEQUENCE demo_core.invoice_number_seq;")]
    [InlineData(DbObjectType.ForeignTable, "demo_extra", "customers_remote", "DROP FOREIGN TABLE demo_extra.customers_remote;")]
    [InlineData(DbObjectType.Type, "demo_core", "order_status", "DROP TYPE demo_core.order_status;")]
    [InlineData(DbObjectType.Domain, "demo_core", "email", "DROP DOMAIN demo_core.email;")]
    [InlineData(DbObjectType.TextSearchDictionary, "demo_extra", "demo_dict", "DROP TEXT SEARCH DICTIONARY demo_extra.demo_dict;")]
    public async Task Скрипт_удаления_имеет_ожидаемый_вид(
        DbObjectType type, string schema, string name, string expected)
    {
        Skip.IfNot(available);

        var drop = await generator.GetDropScriptAsync(connection!, schema, name, type, null, CancellationToken.None);
        Assert.Equal(expected, drop.Trim());
    }

    [SkippableFact]
    public async Task Удаление_перегруженной_функции_требует_сигнатуры()
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;

        // Без сигнатуры скрипт содержит все три перегрузки — неоднозначность видна пользователю.
        var all = await generator.GetDropScriptAsync(connection!, "demo_core", "fn_lookup", DbObjectType.Function, null, ct);
        Assert.Equal(3, all.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        // С сигнатурой — ровно одна перегрузка.
        var one = await generator.GetDropScriptAsync(
            connection!, "demo_core", "fn_lookup", DbObjectType.Function, "p_code text", ct);
        Assert.Equal("DROP FUNCTION demo_core.fn_lookup(p_code text);", one.Trim());
    }

    [SkippableFact]
    public async Task Удаление_одной_перегрузки_не_трогает_остальные()
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;
        var drop = await generator.GetDropScriptAsync(
            connection!, "demo_core", "fn_lookup", DbObjectType.Function, "p_id integer", ct);

        await using var tx = await connection!.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = new NpgsqlCommand(drop, connection, (NpgsqlTransaction)tx))
                await cmd.ExecuteNonQueryAsync(ct);

            await using var check = new NpgsqlCommand(
                """
                SELECT string_agg(pg_get_function_identity_arguments(p.oid), '|' ORDER BY p.oid)
                FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'demo_core' AND p.proname = 'fn_lookup'
                """, connection, (NpgsqlTransaction)tx);

            var remaining = (string?)await check.ExecuteScalarAsync(ct);
            Assert.Equal("p_code text|p_id integer, p_with_email boolean", remaining);
        }
        finally
        {
            await tx.RollbackAsync(ct);
        }
    }

    [SkippableFact]
    public async Task Скрипт_удаления_несуществующего_объекта_даёт_ошибку()
    {
        Skip.IfNot(available);

        await Assert.ThrowsAsync<DdlObjectNotFoundException>(() =>
            generator.GetDropScriptAsync(connection!, "demo_core", "нет_такого", DbObjectType.Table, null, CancellationToken.None));
    }
}
