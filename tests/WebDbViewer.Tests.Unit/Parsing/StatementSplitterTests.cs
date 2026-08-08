using WebDbViewer.Core;
using WebDbViewer.Parsing;

namespace WebDbViewer.Tests.Unit.Parsing;

/// <summary>Тесты разбиения скрипта на statements (PostgreSQL и Oracle).</summary>
public class StatementSplitterTests
{
    private readonly StatementSplitter _splitter = new();

    // ================================================================== PostgreSQL

    [Fact]
    public void Postgres_ПростойСкрипт_РазбиваетсяПоТочкеСЗапятой()
    {
        var result = _splitter.Split("SELECT 1; SELECT 2;\nSELECT 3", DbKind.Postgres);

        Assert.Equal(3, result.Count);
        Assert.Equal("SELECT 1", result[0].Text);
        Assert.Equal("SELECT 2", result[1].Text);
        Assert.Equal("SELECT 3", result[2].Text);
    }

    [Fact]
    public void Postgres_DollarQuoting_ТочкаСЗапятойВнутриНеРазбивает()
    {
        var script = """
            CREATE FUNCTION f() RETURNS void AS $$
            BEGIN
                UPDATE t SET x = 1;
                DELETE FROM t2;
            END
            $$ LANGUAGE plpgsql;
            SELECT 1;
            """;

        var result = _splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.Contains("DELETE FROM t2;", result[0].Text);
        Assert.Equal("SELECT 1", result[1].Text);
    }

    [Fact]
    public void Postgres_DollarQuotingСТегом_УчитываетИменованныйТег()
    {
        // Внутри $body$ встречаются и «;», и $$ — конец только на $body$.
        var script = "CREATE FUNCTION g() RETURNS text AS $body$ SELECT 'a;b' || $$x;y$$ ; $body$ LANGUAGE sql; SELECT 2";

        var result = _splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.StartsWith("CREATE FUNCTION g()", result[0].Text);
        Assert.Equal("SELECT 2", result[1].Text);
    }

    [Fact]
    public void Postgres_ВложенныеКомментарии_ТочкаСЗапятойВнутриИгнорируется()
    {
        var script = "SELECT 1 /* внешний ; /* вложенный ; */ ещё ; */ + 2; SELECT 3";

        var result = _splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.StartsWith("SELECT 1", result[0].Text);
        Assert.Equal("SELECT 3", result[1].Text);
    }

    [Fact]
    public void Postgres_СтрокиИИдентификаторы_НеРазбиваются()
    {
        var script = "SELECT 'a;b', E'c\\';d', \"колонка;странная\" FROM t; SELECT 2";

        var result = _splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 2", result[1].Text);
    }

    [Fact]
    public void Postgres_ОднострочныйКомментарий_ТочкаСЗапятойИгнорируется()
    {
        var script = "SELECT 1 -- комментарий; не конец\n+ 2; SELECT 3";

        var result = _splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Postgres_OffsetИLength_УказываютНаИсходныйТекст()
    {
        var script = "  SELECT 1;\n  SELECT 22  ";

        var result = _splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
        foreach (var st in result)
        {
            // Text должен точно совпадать с подстрокой исходника по Offset/Length.
            Assert.Equal(st.Text, script.Substring(st.Offset, st.Length));
        }
        Assert.Equal(2, result[0].Offset);
        Assert.Equal("SELECT 22", result[1].Text);
    }

    [Fact]
    public void Postgres_ПараметрыДоллара_НеСчитаютсяDollarQuoting()
    {
        var script = "SELECT * FROM t WHERE id = $1; SELECT $2";

        var result = _splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
    }

    // ================================================================== Oracle

    [Fact]
    public void Oracle_PlSqlБлок_ЗавершаетсяСлэшемАНеТочкойСЗапятой()
    {
        var script = """
            BEGIN
                UPDATE t SET x = 1;
                DELETE FROM t2;
            END;
            /
            SELECT * FROM dual
            """;

        var result = _splitter.Split(script, DbKind.Oracle);

        Assert.Equal(2, result.Count);
        Assert.StartsWith("BEGIN", result[0].Text);
        Assert.EndsWith("END;", result[0].Text);
        Assert.Equal("SELECT * FROM dual", result[1].Text);
    }

    [Fact]
    public void Oracle_CreateOrReplaceFunction_ЧитаетсяДоСлэша()
    {
        var script = """
            CREATE OR REPLACE FUNCTION f RETURN NUMBER IS
            BEGIN
                RETURN 1;
            END f;
            /
            CREATE TABLE t (id NUMBER);
            """;

        var result = _splitter.Split(script, DbKind.Oracle);

        Assert.Equal(2, result.Count);
        Assert.Contains("RETURN 1;", result[0].Text);
        Assert.StartsWith("CREATE TABLE t", result[1].Text);
    }

    [Fact]
    public void Oracle_QString_ТочкаСЗапятойВнутриНеРазбивает()
    {
        var script = "SELECT q'[a;b]' FROM dual; SELECT q'{x;y}' FROM dual";

        var result = _splitter.Split(script, DbKind.Oracle);

        Assert.Equal(2, result.Count);
        Assert.Contains("q'[a;b]'", result[0].Text);
        Assert.Contains("q'{x;y}'", result[1].Text);
    }

    [Fact]
    public void Oracle_ОбычныеStatements_РазбиваютсяПоТочкеСЗапятой()
    {
        var script = "INSERT INTO t VALUES (1); UPDATE t SET x = 'a;b'; DELETE FROM t";

        var result = _splitter.Split(script, DbKind.Oracle);

        Assert.Equal(3, result.Count);
        Assert.Contains("'a;b'", result[1].Text);
    }

    [Fact]
    public void Oracle_СлэшВнутриВыражения_НеТерминатор()
    {
        // Деление на строке с другим текстом — не терминатор.
        var script = "SELECT 10 / 2 FROM dual; SELECT 1 FROM dual";

        var result = _splitter.Split(script, DbKind.Oracle);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Oracle_CreatePackage_БлокДоСлэша()
    {
        var script = """
            CREATE OR REPLACE PACKAGE BODY pkg IS
                PROCEDURE p IS
                BEGIN
                    NULL;
                END;
            END pkg;
            /
            """;

        var result = _splitter.Split(script, DbKind.Oracle);

        Assert.Single(result);
        Assert.EndsWith("END pkg;", result[0].Text);
    }

    // ================================================================== Общее

    [Fact]
    public void ПустойСкрипт_ПустойРезультат()
    {
        Assert.Empty(_splitter.Split("", DbKind.Postgres));
        Assert.Empty(_splitter.Split("   \n\t ", DbKind.Postgres));
        Assert.Empty(_splitter.Split(";;;", DbKind.Oracle));
    }
}
