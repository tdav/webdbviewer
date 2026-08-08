using WebDbViewer.Core.Editing;
using WebDbViewer.Providers.Oracle;

namespace WebDbViewer.Tests.Unit.Editing;

/// <summary>Тесты генерации параметризованного DML для Oracle.</summary>
public class OracleDmlGeneratorTests
{
    private readonly OracleDmlGenerator _gen = new();

    private static RowEdit Edit(RowEditKind kind,
        Dictionary<string, object?>? keys = null,
        Dictionary<string, object?>? changed = null) => new()
    {
        Schema = "HR",
        Table = "EMPLOYEES",
        Kind = kind,
        KeyValues = keys ?? new Dictionary<string, object?>(),
        ChangedValues = changed ?? new Dictionary<string, object?>(),
    };

    // ---------------------------------------------------------------- UPDATE

    [Fact]
    public void Update_ByPk_GeneratesParameterizedSql()
    {
        var cmd = _gen.BuildUpdate(DmlTestData.OraEmployees(), Edit(RowEditKind.Update,
            keys: new() { ["EMPLOYEE_ID"] = 100 },
            changed: new() { ["LAST_NAME"] = "Иванов", ["SALARY"] = 5000 }));

        Assert.Equal(
            "UPDATE HR.EMPLOYEES SET LAST_NAME = :s0, SALARY = :s1 WHERE EMPLOYEE_ID = :k2",
            cmd.Sql);
        Assert.Equal(["s0", "s1", "k2"], cmd.Parameters.Select(p => p.Name));
        Assert.Equal(["Иванов", 5000, 100], cmd.Parameters.Select(p => p.Value).Cast<object?>());
        // Значения не попадают в текст SQL.
        Assert.DoesNotContain("Иванов", cmd.Sql);
        Assert.DoesNotContain("5000", cmd.Sql);
    }

    [Theory]
    [InlineData("ROWID")]
    [InlineData("__ROWID")] // алиас псевдоколонки из SELECT-страницы данных
    [InlineData("rowid")]
    public void Update_WithoutPk_UsesRowId(string keyName)
    {
        var cmd = _gen.BuildUpdate(DmlTestData.OraNoPk(), Edit(RowEditKind.Update,
            keys: new() { [keyName] = "AAAR3sAAEAAAACXAAA" },
            changed: new() { ["MESSAGE"] = "текст" }));

        Assert.Contains("WHERE ROWID = :k1", cmd.Sql);
        Assert.Equal("AAAR3sAAEAAAACXAAA", cmd.Parameters.Single(p => p.Name == "k1").Value);
    }

    [Fact]
    public void Update_NullKeyValue_BecomesIsNull()
    {
        var cmd = _gen.BuildUpdate(DmlTestData.OraEmployees(), Edit(RowEditKind.Update,
            keys: new() { ["EMPLOYEE_ID"] = 1, ["LAST_NAME"] = null },
            changed: new() { ["SALARY"] = 1000 }));

        Assert.Contains("LAST_NAME IS NULL", cmd.Sql);
        Assert.Equal(2, cmd.Parameters.Count); // NULL-предикат без параметра
    }

    [Fact]
    public void Update_WithoutKeys_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildUpdate(DmlTestData.OraEmployees(), Edit(RowEditKind.Update,
                changed: new() { ["SALARY"] = 1 })));

    [Fact]
    public void Update_GeneratedColumn_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildUpdate(DmlTestData.OraEmployees(), Edit(RowEditKind.Update,
                keys: new() { ["EMPLOYEE_ID"] = 1 },
                changed: new() { ["FULL_NAME"] = "x" })));

    [Fact]
    public void Update_MixedCaseIdentifiers_Quoted()
    {
        var cmd = _gen.BuildUpdate(DmlTestData.OraQuoted(), Edit(RowEditKind.Update,
            keys: new() { ["Id"] = 1 },
            changed: new() { ["Qty"] = 3 }));

        // Смешанный регистр в Oracle требует кавычек (иначе имя приводится к UPPERCASE).
        Assert.Equal(
            "UPDATE \"MyApp\".\"Order Items\" SET \"Qty\" = :s0 WHERE \"Id\" = :k1",
            cmd.Sql);
    }

    // ---------------------------------------------------------------- INSERT

    [Fact]
    public void Insert_GeneratesColumnsAndParameters()
    {
        var cmd = _gen.BuildInsert(DmlTestData.OraEmployees(), Edit(RowEditKind.Insert,
            changed: new() { ["EMPLOYEE_ID"] = 200, ["LAST_NAME"] = "Петров" }));

        Assert.Equal(
            "INSERT INTO HR.EMPLOYEES (EMPLOYEE_ID, LAST_NAME) VALUES (:s0, :s1)",
            cmd.Sql);
        Assert.Equal([200, "Петров"], cmd.Parameters.Select(p => p.Value).Cast<object?>());
    }

    [Fact]
    public void Insert_EmptyValues_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildInsert(DmlTestData.OraEmployees(), Edit(RowEditKind.Insert)));

    // ---------------------------------------------------------------- DELETE

    [Fact]
    public void Delete_ByPk()
    {
        var cmd = _gen.BuildDelete(DmlTestData.OraEmployees(), Edit(RowEditKind.Delete,
            keys: new() { ["EMPLOYEE_ID"] = 300 }));

        Assert.Equal("DELETE FROM HR.EMPLOYEES WHERE EMPLOYEE_ID = :k0", cmd.Sql);
        Assert.Equal(300, cmd.Parameters.Single().Value);
    }

    [Fact]
    public void Delete_WithoutPk_UsesRowId()
    {
        var cmd = _gen.BuildDelete(DmlTestData.OraNoPk(), Edit(RowEditKind.Delete,
            keys: new() { ["__ROWID"] = "AAAR3sAAEAAAACXAAB" }));

        Assert.Equal("DELETE FROM HR.AUDIT_LOG WHERE ROWID = :k0", cmd.Sql);
        Assert.Equal("AAAR3sAAEAAAACXAAB", cmd.Parameters.Single().Value);
    }

    [Fact]
    public void Delete_WithoutKeys_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildDelete(DmlTestData.OraEmployees(), Edit(RowEditKind.Delete)));

    // ---------------------------------------------------------------- Общее

    [Fact]
    public void WrongKind_Throws()
        => Assert.Throws<InvalidOperationException>(() =>
            _gen.BuildInsert(DmlTestData.OraEmployees(), Edit(RowEditKind.Update,
                changed: new() { ["SALARY"] = 1 })));

    [Fact]
    public void Dispatch_Build_RoutesByKind()
    {
        var update = _gen.Build(DmlTestData.OraEmployees(), Edit(RowEditKind.Update,
            keys: new() { ["EMPLOYEE_ID"] = 1 }, changed: new() { ["SALARY"] = 2 }));
        Assert.StartsWith("UPDATE HR.EMPLOYEES", update.Sql);
    }
}
