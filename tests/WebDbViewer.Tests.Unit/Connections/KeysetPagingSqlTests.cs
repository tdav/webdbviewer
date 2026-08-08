using WebDbViewer.Core;
using WebDbViewer.Providers.Oracle;
using WebDbViewer.Providers.Postgres;

namespace WebDbViewer.Tests.Unit.Connections;

/// <summary>Тесты генерации SELECT с keyset-пагинацией для обоих диалектов.</summary>
public class KeysetPagingSqlTests
{
    private static TableInfo PgTable(params string[] pk) => new()
    {
        Schema = "public",
        Name = "orders",
        Type = DbObjectType.Table,
        Columns =
        [
            new ColumnInfo { Name = "id", DataType = "bigint", IsPrimaryKey = pk.Contains("id") },
            new ColumnInfo { Name = "created_at", DataType = "timestamptz", IsPrimaryKey = pk.Contains("created_at") },
            new ColumnInfo { Name = "amount", DataType = "numeric" },
        ],
        PrimaryKeyColumns = pk,
    };

    private static TableInfo OraTable(params string[] pk) => new()
    {
        Schema = "HR",
        Name = "ORDERS",
        Type = DbObjectType.Table,
        Columns =
        [
            new ColumnInfo { Name = "ID", DataType = "NUMBER(19)", IsPrimaryKey = pk.Contains("ID") },
            new ColumnInfo { Name = "CREATED_AT", DataType = "TIMESTAMP(6)", IsPrimaryKey = pk.Contains("CREATED_AT") },
            new ColumnInfo { Name = "AMOUNT", DataType = "NUMBER(10,2)" },
        ],
        PrimaryKeyColumns = pk,
    };

    // ---------------------------------------------------------------- PostgreSQL

    [Fact]
    public void Pg_FirstPage_SinglePk_OrderByPkWithLimit()
    {
        var sql = new PostgresProvider().BuildSelectPageSql(PgTable("id"), new DataPageRequest
        {
            Schema = "public",
            Table = "orders",
            Limit = 100,
        });

        Assert.Equal("SELECT id, created_at, amount FROM public.orders ORDER BY id LIMIT 100", sql);
    }

    [Fact]
    public void Pg_NextPage_CompositePk_TupleComparison()
    {
        var sql = new PostgresProvider().BuildSelectPageSql(PgTable("id", "created_at"), new DataPageRequest
        {
            Schema = "public",
            Table = "orders",
            Limit = 50,
            After = [42L, "2026-01-01"],
        });

        Assert.Contains("WHERE (id, created_at) > (@after0, @after1)", sql);
        Assert.Contains("ORDER BY id, created_at", sql);
        Assert.EndsWith("LIMIT 50", sql);
    }

    [Fact]
    public void Pg_Descending_InvertsOperatorAndOrder()
    {
        var sql = new PostgresProvider().BuildSelectPageSql(PgTable("id"), new DataPageRequest
        {
            Schema = "public",
            Table = "orders",
            OrderDescending = true,
            After = [42L],
        });

        Assert.Contains("(id) < (@after0)", sql);
        Assert.Contains("ORDER BY id DESC", sql);
    }

    [Fact]
    public void Pg_NoPk_UsesCtid()
    {
        var sql = new PostgresProvider().BuildSelectPageSql(PgTable(), new DataPageRequest
        {
            Schema = "public",
            Table = "orders",
            After = ["(0,1)"],
        });

        Assert.StartsWith("SELECT ctid, id, created_at, amount FROM public.orders", sql);
        Assert.Contains("(ctid) > (@after0)", sql);
        Assert.Contains("ORDER BY ctid", sql);
    }

    [Fact]
    public void Pg_ExplicitOrderBy_AddsPkAsTiebreaker()
    {
        var sql = new PostgresProvider().BuildSelectPageSql(PgTable("id"), new DataPageRequest
        {
            Schema = "public",
            Table = "orders",
            OrderBy = "created_at",
            After = ["2026-01-01", 42L],
        });

        Assert.Contains("(created_at, id) > (@after0, @after1)", sql);
        Assert.Contains("ORDER BY created_at, id", sql);
    }

    [Fact]
    public void Pg_WhereFilter_Wrapped_And_CombinedWithKeyset()
    {
        var sql = new PostgresProvider().BuildSelectPageSql(PgTable("id"), new DataPageRequest
        {
            Schema = "public",
            Table = "orders",
            WhereFilter = "amount > 100",
            After = [7L],
        });

        Assert.Contains("WHERE (amount > 100) AND (id) > (@after0)", sql);
    }

    [Fact]
    public void Pg_MixedCaseNames_AreQuoted()
    {
        var table = new TableInfo
        {
            Schema = "Public",
            Name = "MyOrders",
            Type = DbObjectType.Table,
            Columns = [new ColumnInfo { Name = "Id", DataType = "bigint", IsPrimaryKey = true }],
            PrimaryKeyColumns = ["Id"],
        };
        var sql = new PostgresProvider().BuildSelectPageSql(table, new DataPageRequest { Schema = "Public", Table = "MyOrders" });

        Assert.Contains("FROM \"Public\".\"MyOrders\"", sql);
        Assert.Contains("ORDER BY \"Id\"", sql);
    }

    // ---------------------------------------------------------------- Oracle

    [Fact]
    public void Ora_FirstPage_SinglePk_FetchFirst()
    {
        var sql = new OracleProvider().BuildSelectPageSql(OraTable("ID"), new DataPageRequest
        {
            Schema = "HR",
            Table = "ORDERS",
            Limit = 100,
        });

        Assert.Equal("SELECT ID, CREATED_AT, AMOUNT FROM HR.ORDERS ORDER BY ID FETCH FIRST 100 ROWS ONLY", sql);
    }

    [Fact]
    public void Ora_NextPage_CompositePk_ExpandedDisjunction()
    {
        var sql = new OracleProvider().BuildSelectPageSql(OraTable("ID", "CREATED_AT"), new DataPageRequest
        {
            Schema = "HR",
            Table = "ORDERS",
            Limit = 50,
            After = [42L, "2026-01-01"],
        });

        // Oracle не поддерживает кортежное сравнение — ожидаем развёрнутую дизъюнкцию.
        Assert.Contains("((ID > :after0) OR (ID = :after0 AND CREATED_AT > :after1))", sql);
        Assert.Contains("ORDER BY ID, CREATED_AT", sql);
        Assert.EndsWith("FETCH FIRST 50 ROWS ONLY", sql);
    }

    [Fact]
    public void Ora_Descending_InvertsOperatorAndOrder()
    {
        var sql = new OracleProvider().BuildSelectPageSql(OraTable("ID"), new DataPageRequest
        {
            Schema = "HR",
            Table = "ORDERS",
            OrderDescending = true,
            After = [42L],
        });

        Assert.Contains("(ID < :after0)", sql);
        Assert.Contains("ORDER BY ID DESC", sql);
    }

    [Fact]
    public void Ora_NoPk_UsesRowid()
    {
        var sql = new OracleProvider().BuildSelectPageSql(OraTable(), new DataPageRequest
        {
            Schema = "HR",
            Table = "ORDERS",
            After = ["AAA"],
        });

        Assert.StartsWith("SELECT ROWID AS \"__ROWID\", ID, CREATED_AT, AMOUNT FROM HR.ORDERS", sql);
        Assert.Contains("(ROWID > :after0)", sql);
        Assert.Contains("ORDER BY ROWID", sql);
    }

    [Fact]
    public void Ora_LowercaseNames_AreQuoted()
    {
        var table = new TableInfo
        {
            Schema = "hr",
            Name = "orders",
            Type = DbObjectType.Table,
            Columns = [new ColumnInfo { Name = "id", DataType = "NUMBER", IsPrimaryKey = true }],
            PrimaryKeyColumns = ["id"],
        };
        var sql = new OracleProvider().BuildSelectPageSql(table, new DataPageRequest { Schema = "hr", Table = "orders" });

        Assert.Contains("FROM \"hr\".\"orders\"", sql);
        Assert.Contains("ORDER BY \"id\"", sql);
    }

    [Fact]
    public void Ora_WhereFilter_CombinedWithKeyset()
    {
        var sql = new OracleProvider().BuildSelectPageSql(OraTable("ID"), new DataPageRequest
        {
            Schema = "HR",
            Table = "ORDERS",
            WhereFilter = "AMOUNT > 100",
            After = [7L],
        });

        Assert.Contains("WHERE (AMOUNT > 100) AND ((ID > :after0))", sql);
    }
}
