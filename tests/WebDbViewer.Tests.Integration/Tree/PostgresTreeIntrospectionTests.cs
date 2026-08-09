using System.Data.Common;
using Npgsql;
using WebDbViewer.Core;
using WebDbViewer.Providers.Postgres;

namespace WebDbViewer.Tests.Integration.Tree;

/// <summary>
/// Интроспекция дерева объектов на демонстрационной базе (tools/testdata/pg_demo.sql).
/// Строка подключения переопределяется через WEBDBVIEWER_TEST_DEMO_DB.
/// Если базы нет или в ней нет схемы demo_core — тесты пропускаются.
/// </summary>
public sealed class PostgresTreeIntrospectionTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=webdbviewer_demo;Username=postgres;Password=1;Pooling=true;";

    private readonly string connectionString =
        Environment.GetEnvironmentVariable("WEBDBVIEWER_TEST_DEMO_DB") ?? DefaultConnectionString;

    private readonly PostgresProvider provider = new();
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

    private DbConnection Db => connection!;

    // Ожидаемый состав фикстуры: категория -> количество объектов.
    public static TheoryData<string, string, int> ExpectedCategories() => new()
    {
        { "demo_core", "Tables", 18 },
        { "demo_core", "Views", 5 },
        { "demo_core", "MaterializedViews", 2 },
        { "demo_core", "Indexes", 31 },
        { "demo_core", "Sequences", 9 },
        { "demo_core", "Functions", 21 },
        { "demo_core", "Procedures", 2 },
        { "demo_core", "Aggregates", 1 },
        { "demo_core", "Triggers", 6 },
        { "demo_core", "Types", 3 },
        { "demo_core", "Domains", 2 },
        { "demo_core", "Rules", 1 },
        { "demo_extra", "Tables", 4 },
        { "demo_extra", "ForeignTables", 1 },
        { "demo_extra", "Views", 1 },
        { "demo_extra", "Indexes", 4 },
        { "demo_extra", "Sequences", 3 },
        { "demo_extra", "Functions", 2 },
        { "demo_extra", "Procedures", 2 },
        { "demo_extra", "Operators", 1 },
        { "demo_extra", "Collations", 2 },
        { "demo_extra", "TextSearchConfigs", 1 },
        { "demo_extra", "TextSearchDictionaries", 1 },
        { "demo_extra", "Policies", 2 },
    };

    [SkippableTheory]
    [MemberData(nameof(ExpectedCategories))]
    public async Task Категория_содержит_ожидаемое_число_объектов(string schema, string category, int expected)
    {
        Skip.IfNot(available);

        var children = await provider.GetChildrenAsync(Db, [schema, category], includeSystem: false, CancellationToken.None);
        Assert.Equal(expected, children.Count);
    }

    [SkippableFact]
    public async Task Папки_категорий_показываются_только_непустые_и_со_счётчиком()
    {
        Skip.IfNot(available);

        var folders = await provider.GetChildrenAsync(Db, ["demo_core"], includeSystem: false, CancellationToken.None);

        Assert.All(folders, f =>
        {
            Assert.Equal("folder", f.Attributes?["kind"]);
            Assert.False(string.IsNullOrEmpty(f.Attributes?["title"]));
            Assert.True(int.Parse(f.Attributes!["count"]) > 0);
        });

        var keys = folders.Select(f => f.Name).ToList();
        // В demo_core нет операторов, коллаций, политик, FTS и внешних таблиц — папок быть не должно.
        Assert.DoesNotContain("Operators", keys);
        Assert.DoesNotContain("Collations", keys);
        Assert.DoesNotContain("Policies", keys);
        Assert.DoesNotContain("ForeignTables", keys);
        Assert.Contains("Tables", keys);
        Assert.Contains("Triggers", keys);
    }

    [SkippableFact]
    public async Task Перегрузки_функции_различаются_сигнатурой()
    {
        Skip.IfNot(available);

        var functions = await provider.GetChildrenAsync(Db, ["demo_core", "Functions"], includeSystem: false, CancellationToken.None);
        var overloads = functions.Where(f => f.Name == "fn_lookup").ToList();

        Assert.Equal(3, overloads.Count);
        var signatures = overloads.Select(o => o.Attributes!["signature"]).ToList();
        Assert.Equal(signatures.Count, signatures.Distinct(StringComparer.Ordinal).Count());
        // pg_get_function_identity_arguments сохраняет имена параметров — это и показывается в дереве.
        Assert.Contains("p_id integer", signatures);
        Assert.Contains("p_code text", signatures);
        Assert.Contains("p_id integer, p_with_email boolean", signatures);
    }

    [SkippableFact]
    public async Task Триггер_знает_свою_таблицу_и_является_листом()
    {
        Skip.IfNot(available);

        var triggers = await provider.GetChildrenAsync(Db, ["demo_core", "Triggers"], includeSystem: false, CancellationToken.None);
        var trigger = Assert.Single(triggers, t => t.Name == "tg_products_search");

        Assert.Equal("products", trigger.Attributes!["table"]);
        Assert.False(trigger.HasChildren);
    }

    [SkippableFact]
    public async Task Колонки_запрашиваются_только_у_отношений()
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;

        var columns = await provider.GetChildrenAsync(Db, ["demo_core", "Tables", "customers"], includeSystem: false, ct);
        Assert.Contains(columns, c => c.Name == "full_name" && c.Type == DbObjectType.Column);

        // У индекса детей нет: раньше уровень 3 всегда трактовался как «колонки объекта».
        var indexChildren = await provider.GetChildrenAsync(Db, ["demo_core", "Indexes", "ix_customers_last_name"], includeSystem: false, ct);
        Assert.Empty(indexChildren);
    }

    [SkippableFact]
    public async Task Внешняя_таблица_отдаёт_колонки()
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;
        var foreignTables = await provider.GetChildrenAsync(Db, ["demo_extra", "ForeignTables"], includeSystem: false, ct);
        var remote = Assert.Single(foreignTables);

        Assert.Equal(DbObjectType.ForeignTable, remote.Type);
        Assert.True(remote.HasChildren);

        var columns = await provider.GetChildrenAsync(Db, ["demo_extra", "ForeignTables", remote.Name], includeSystem: false, ct);
        Assert.Equal(4, columns.Count);
    }

    [SkippableTheory]
    [InlineData("CREATE TYPE demo_core.tmp_version_enum AS ENUM ('a')")]
    [InlineData("CREATE FUNCTION demo_core.tmp_version_fn() RETURNS int LANGUAGE sql AS 'SELECT 1'")]
    [InlineData("CREATE TRIGGER tmp_version_trg AFTER INSERT ON demo_core.settings FOR EACH STATEMENT EXECUTE FUNCTION demo_core.trg_stmt_audit()")]
    public async Task Версия_схемы_меняется_при_создании_объекта_любого_типа(string ddl)
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;
        var before = await provider.GetSchemaVersionAsync(Db, "demo_core", ct);

        // Транзакция откатывается: демо-база остаётся без временных объектов.
        await using var tx = await connection!.BeginTransactionAsync(ct);
        try
        {
            await using (var create = new NpgsqlCommand(ddl, connection, (NpgsqlTransaction)tx))
                await create.ExecuteNonQueryAsync(ct);

            var after = await provider.GetSchemaVersionAsync(Db, "demo_core", ct);
            Assert.NotEqual(before, after);
        }
        finally
        {
            await tx.RollbackAsync(ct);
        }
    }

    [SkippableFact]
    public async Task Одноимённые_таблицы_разных_схем_не_смешиваются()
    {
        Skip.IfNot(available);

        var ct = CancellationToken.None;
        var core = await provider.GetChildrenAsync(Db, ["demo_core", "Tables", "settings"], includeSystem: false, ct);
        var extra = await provider.GetChildrenAsync(Db, ["demo_extra", "Tables", "settings"], includeSystem: false, ct);

        Assert.Equal(2, core.Count);            // key, value
        Assert.Equal(3, extra.Count);           // key, value, scope
        Assert.Contains(extra, c => c.Name == "scope");
        Assert.DoesNotContain(core, c => c.Name == "scope");
    }
}
