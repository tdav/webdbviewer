using WebDbViewer.Core;
using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Unit.Api;

/// <summary>Тесты наивного фолбэк-сплиттера и выбора statement под курсором.</summary>
public class NaiveStatementSplitterTests
{
    private static readonly NaiveStatementSplitter Splitter = NaiveStatementSplitter.Instance;

    [Fact]
    public void Split_ДваStatement_КорректныеТекстыИСмещения()
    {
        const string script = "SELECT 1;\nSELECT 2;";

        var result = Splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 1", result[0].Text);
        Assert.Equal(0, result[0].Offset);
        Assert.Equal("SELECT 2", result[1].Text);
        Assert.Equal(script.IndexOf("SELECT 2", StringComparison.Ordinal), result[1].Offset);
    }

    [Fact]
    public void Split_ТочкаСЗапятойВСтроковомЛитерале_НеРазбивает()
    {
        var result = Splitter.Split("SELECT 'a;b';SELECT 2", DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 'a;b'", result[0].Text);
    }

    [Fact]
    public void Split_ЭкранированнаяКавычкаВЛитерале_НеЛомаетРазбор()
    {
        var result = Splitter.Split("SELECT 'it''s;ok';SELECT 2", DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT 'it''s;ok'", result[0].Text);
    }

    [Fact]
    public void Split_ТочкаСЗапятойВКвотированномИдентификаторе_НеРазбивает()
    {
        var result = Splitter.Split("SELECT \"a;b\" FROM t;SELECT 2", DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.Equal("SELECT \"a;b\" FROM t", result[0].Text);
    }

    [Fact]
    public void Split_ТочкаСЗапятойВКомментариях_НеРазбивает()
    {
        var result = Splitter.Split("SELECT 1 -- x;y\n;SELECT 2 /* a;b */;", DbKind.Postgres);

        Assert.Equal(2, result.Count);
        Assert.StartsWith("SELECT 1", result[0].Text);
        Assert.StartsWith("SELECT 2", result[1].Text);
    }

    [Fact]
    public void Split_ПустыеStatementsПропускаются()
    {
        var result = Splitter.Split(" ; ;SELECT 1; ;", DbKind.Postgres);

        Assert.Single(result);
        Assert.Equal("SELECT 1", result[0].Text);
    }

    [Fact]
    public void Split_ПустойСкрипт_ПустойСписок()
    {
        Assert.Empty(Splitter.Split("", DbKind.Postgres));
        Assert.Empty(Splitter.Split("   \n\t ", DbKind.Oracle));
    }

    [Fact]
    public void Split_OffsetИLength_УказываютНаТекстВИсходномСкрипте()
    {
        const string script = "  SELECT 1  ;  UPDATE t SET x = 1  ";

        var result = Splitter.Split(script, DbKind.Postgres);

        Assert.Equal(2, result.Count);
        foreach (var st in result)
            Assert.Equal(st.Text, script.Substring(st.Offset, st.Length));
    }

    // ---------------- Выбор statement под курсором ----------------

    [Fact]
    public void FindStatementAtCaret_КареткаВнутриВторого_ВозвращаетВторой()
    {
        var statements = Splitter.Split("SELECT 1;\nSELECT 2;", DbKind.Postgres);

        var st = QueryEndpoints.FindStatementAtCaret(statements, statements[1].Offset + 3);

        Assert.Equal("SELECT 2", st.Text);
    }

    [Fact]
    public void FindStatementAtCaret_КареткаВПробелахПослеStatement_ВозвращаетПредыдущий()
    {
        const string script = "SELECT 1;\n\n\nSELECT 2;";
        var statements = Splitter.Split(script, DbKind.Postgres);

        // Каретка на пустой строке между statements (после «;» первого).
        var st = QueryEndpoints.FindStatementAtCaret(statements, 10);

        Assert.Equal("SELECT 1", st.Text);
    }

    [Fact]
    public void FindStatementAtCaret_КареткаПередПервым_ВозвращаетПервый()
    {
        var statements = Splitter.Split("   SELECT 1;", DbKind.Postgres);

        var st = QueryEndpoints.FindStatementAtCaret(statements, 0);

        Assert.Equal("SELECT 1", st.Text);
    }
}
