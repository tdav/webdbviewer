using WebDbViewer.Core.Editing;
using WebDbViewer.Providers.Postgres;

namespace WebDbViewer.Tests.Unit.Editing;

/// <summary>Тесты генерации параметризованного DML для PostgreSQL.</summary>
public class PgDmlGeneratorTests
{
    private readonly PgDmlGenerator _gen = new();

    private static RowEdit Edit(RowEditKind kind,
        Dictionary<string, object?>? keys = null,
        Dictionary<string, object?>? changed = null) => new()
    {
        Schema = "public",
        Table = "users",
        Kind = kind,
        KeyValues = keys ?? new Dictionary<string, object?>(),
        ChangedValues = changed ?? new Dictionary<string, object?>(),
    };

    // ---------------------------------------------------------------- UPDATE

    [Fact]
    public void Update_ByPk_GeneratesParameterizedSql()
    {
        var cmd = _gen.BuildUpdate(DmlTestData.PgUsers(), Edit(RowEditKind.Update,
            keys: new() { ["id"] = 42 },
            changed: new() { ["name"] = "Иван", ["age"] = "33" }));

        Assert.Equal(
            "UPDATE public.users SET name = CAST(@s0 AS text), age = CAST(@s1 AS integer) WHERE id = CAST(@k2 AS integer)",
            cmd.Sql);
        Assert.Equal(["s0", "s1", "k2"], cmd.Parameters.Select(p => p.Name));
        Assert.Equal(["Иван", "33", 42], cmd.Parameters.Select(p => p.Value).Cast<object?>());
        // Значения не должны попадать в текст SQL (только параметры).
        Assert.DoesNotContain("Иван", cmd.Sql);
        Assert.DoesNotContain("42", cmd.Sql);
    }

    [Fact]
    public void Update_WithoutPk_UsesCtid()
    {
        var cmd = _gen.BuildUpdate(DmlTestData.PgNoPk(), Edit(RowEditKind.Update,
            keys: new() { ["ctid"] = "(0,7)" },
            changed: new() { ["message"] = "новый текст" }));

        Assert.Contains("WHERE ctid = CAST(@k1 AS tid)", cmd.Sql);
        Assert.Equal("(0,7)", cmd.Parameters.Single(p => p.Name == "k1").Value);
    }

    [Fact]
    public void Update_NullKeyValue_BecomesIsNull()
    {
        var table = DmlTestData.PgUsers();
        var cmd = _gen.BuildUpdate(table, Edit(RowEditKind.Update,
            keys: new() { ["id"] = 1, ["name"] = null },
            changed: new() { ["age"] = 20 }));

        Assert.Contains("name IS NULL", cmd.Sql);
        // Для NULL-предиката параметр не создаётся.
        Assert.Equal(2, cmd.Parameters.Count);
    }

    [Fact]
    public void Update_WithoutKeys_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildUpdate(DmlTestData.PgUsers(), Edit(RowEditKind.Update,
                changed: new() { ["name"] = "x" })));

    [Fact]
    public void Update_WithoutChangedValues_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildUpdate(DmlTestData.PgUsers(), Edit(RowEditKind.Update,
                keys: new() { ["id"] = 1 })));

    [Fact]
    public void Update_GeneratedColumn_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildUpdate(DmlTestData.PgUsers(), Edit(RowEditKind.Update,
                keys: new() { ["id"] = 1 },
                changed: new() { ["full_name"] = "x" })));

    [Fact]
    public void Update_UnknownColumn_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildUpdate(DmlTestData.PgUsers(), Edit(RowEditKind.Update,
                keys: new() { ["id"] = 1 },
                changed: new() { ["no_such"] = "x" })));

    [Fact]
    public void Update_QuotedIdentifiers_CompositePk()
    {
        var cmd = _gen.BuildUpdate(DmlTestData.PgQuoted(), Edit(RowEditKind.Update,
            keys: new() { ["Order Id"] = 1, ["select"] = 2 },
            changed: new() { ["Qty"] = 5 }));

        Assert.Contains("UPDATE \"MySchema\".\"Order Items\" SET \"Qty\" = ", cmd.Sql);
        Assert.Contains("\"Order Id\" = ", cmd.Sql);
        Assert.Contains("\"select\" = ", cmd.Sql);
        Assert.Contains(" AND ", cmd.Sql);
    }

    // ---------------------------------------------------------------- INSERT

    [Fact]
    public void Insert_GeneratesColumnsAndParameters()
    {
        var cmd = _gen.BuildInsert(DmlTestData.PgUsers(), Edit(RowEditKind.Insert,
            changed: new() { ["name"] = "Пётр", ["age"] = "25" }));

        Assert.Equal(
            "INSERT INTO public.users (name, age) VALUES (CAST(@s0 AS text), CAST(@s1 AS integer))",
            cmd.Sql);
        Assert.Equal(["Пётр", "25"], cmd.Parameters.Select(p => p.Value).Cast<object?>());
    }

    [Fact]
    public void Insert_EmptyValues_DefaultValues()
    {
        var cmd = _gen.BuildInsert(DmlTestData.PgUsers(), Edit(RowEditKind.Insert));
        Assert.Equal("INSERT INTO public.users DEFAULT VALUES", cmd.Sql);
        Assert.Empty(cmd.Parameters);
    }

    [Fact]
    public void Insert_GeneratedColumn_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildInsert(DmlTestData.PgUsers(), Edit(RowEditKind.Insert,
                changed: new() { ["full_name"] = "x" })));

    // ---------------------------------------------------------------- DELETE

    [Fact]
    public void Delete_ByPk()
    {
        var cmd = _gen.BuildDelete(DmlTestData.PgUsers(), Edit(RowEditKind.Delete,
            keys: new() { ["id"] = 7 }));

        Assert.Equal("DELETE FROM public.users WHERE id = CAST(@k0 AS integer)", cmd.Sql);
        Assert.Equal(7, cmd.Parameters.Single().Value);
    }

    [Fact]
    public void Delete_WithoutPk_UsesCtid()
    {
        var cmd = _gen.BuildDelete(DmlTestData.PgNoPk(), Edit(RowEditKind.Delete,
            keys: new() { ["ctid"] = "(12,3)" }));

        Assert.Equal("DELETE FROM public.logs WHERE ctid = CAST(@k0 AS tid)", cmd.Sql);
        Assert.Equal("(12,3)", cmd.Parameters.Single().Value);
    }

    [Fact]
    public void Delete_WithoutKeys_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildDelete(DmlTestData.PgUsers(), Edit(RowEditKind.Delete)));

    // ---------------------------------------------------------------- Общее

    [Fact]
    public void WrongKind_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildUpdate(DmlTestData.PgUsers(), Edit(RowEditKind.Delete,
                keys: new() { ["id"] = 1 }, changed: new() { ["name"] = "x" })));

    [Fact]
    public void Dispatch_Build_RoutesByKind()
    {
        var insert = _gen.Build(DmlTestData.PgUsers(), Edit(RowEditKind.Insert, changed: new() { ["name"] = "a" }));
        var delete = _gen.Build(DmlTestData.PgUsers(), Edit(RowEditKind.Delete, keys: new() { ["id"] = 1 }));
        Assert.StartsWith("INSERT INTO", insert.Sql);
        Assert.StartsWith("DELETE FROM", delete.Sql);
    }

    [Fact]
    public void CreateDbCommand_PopulatesDbParameters()
    {
        var cmd = _gen.BuildUpdate(DmlTestData.PgUsers(), Edit(RowEditKind.Update,
            keys: new() { ["id"] = 5 },
            changed: new() { ["name"] = null }));

        using var connection = new Npgsql.NpgsqlConnection();
        using var dbCmd = cmd.CreateDbCommand(connection);

        Assert.Equal(cmd.Sql, dbCmd.CommandText);
        Assert.Equal(2, dbCmd.Parameters.Count);
        // NULL-значение передаётся как DBNull.
        Assert.Equal(DBNull.Value, dbCmd.Parameters[0].Value);
        Assert.Equal(5, dbCmd.Parameters[1].Value);
    }
}
