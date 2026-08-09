using WebDbViewer.Completion;
using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Completion;

/// <summary>
/// Встроенные средства диалекта в подсказках: функции, константы и псевдоколонки,
/// типы данных в позиции приведения, Oracle-специфика.
/// </summary>
public class DialectFeatureTests
{
    private readonly CompletionEngine _engine = new(new FakeMetadataCache());

    private async Task<IReadOnlyList<CompletionItem>> CompleteAsync(
        string sqlWithCaret, DbKind dialect = DbKind.Postgres)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "В тестовом SQL нет маркера каретки «|»");
        var sql = sqlWithCaret.Remove(caret, 1);

        return await _engine.CompleteAsync(new CompletionRequest
        {
            DataSourceId = FakeMetadataCache.DsId,
            SqlText = sql,
            CaretOffset = caret,
            DefaultSchema = "public",
            Limit = 1000, // встроенных функций много; лимит не должен скрывать проверяемое
        }, dialect, CancellationToken.None);
    }

    // ================================================================== Встроенные функции

    [Fact]
    public async Task Postgres_ВSelect_ПредлагаютсяВстроенныеФункции()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        var labels = items.Where(i => i.Kind == "function").Select(i => i.Label).ToList();
        Assert.Contains("coalesce", labels);
        Assert.Contains("string_agg", labels);
    }

    [Fact]
    public async Task Oracle_ВSelect_ПредлагаютсяВстроенныеФункции()
    {
        var items = await CompleteAsync("SELECT | FROM users", DbKind.Oracle);

        var labels = items.Where(i => i.Kind == "function").Select(i => i.Label).ToList();
        Assert.Contains("NVL", labels);
        Assert.Contains("LISTAGG", labels);
    }

    [Fact]
    public async Task ВстроеннаяФункция_ВставляетсяСоСкобкойИНесётОписание()
    {
        var items = await CompleteAsync("SELECT NVL| FROM users", DbKind.Oracle);

        var nvl = items.First(i => i.Label == "NVL");
        Assert.Equal("NVL(", nvl.InsertText);
        Assert.Equal("NVL(expr, replacement)", nvl.Detail);
        Assert.Contains("NULL", nvl.Documentation);
    }

    [Fact]
    public async Task ФункцииДиалектов_НеПеремешиваются()
    {
        var oracle = await CompleteAsync("SELECT | FROM users", DbKind.Oracle);
        var postgres = await CompleteAsync("SELECT | FROM users");

        Assert.DoesNotContain(oracle, i => i.Label == "string_agg");
        Assert.DoesNotContain(postgres, i => i.Label == "NVL");
    }

    [Fact]
    public async Task ВстроенныеФункции_НижеПользовательских()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        var user = items.First(i => i.Label == "calc_total");
        var builtin = items.First(i => i.Label == "coalesce");
        Assert.True(user.SortPriority < builtin.SortPriority);
    }

    // ================================================================== Константы и псевдоколонки

    [Fact]
    public async Task Oracle_ПсевдоколонкиИКонстанты_ПредлагаютсяБезСкобок()
    {
        var items = await CompleteAsync("SELECT | FROM users", DbKind.Oracle);

        var sysdate = items.First(i => i.Label == "SYSDATE");
        Assert.Equal("constant", sysdate.Kind);
        Assert.Equal("SYSDATE", sysdate.InsertText);

        Assert.Contains(items, i => i.Label == "ROWNUM" && i.Kind == "constant");
        Assert.Contains(items, i => i.Label == "LEVEL" && i.Kind == "constant");
    }

    [Fact]
    public async Task Postgres_КонстантыВремени_Предлагаются()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        Assert.Contains(items, i => i.Label == "current_date" && i.Kind == "constant");
        Assert.Contains(items, i => i.Label == "current_user" && i.Kind == "constant");
    }

    // ================================================================== Типы данных

    [Fact]
    public async Task Postgres_ПослеДвойногоДвоеточия_ПредлагаютсяТипы()
    {
        var items = await CompleteAsync("SELECT id::| FROM users");

        var types = items.Where(i => i.Kind == "type").Select(i => i.Label).ToList();
        Assert.Contains("integer", types);
        Assert.Contains("jsonb", types);
    }

    [Fact]
    public async Task Postgres_ПослеДвойногоДвоеточия_КолонокНет()
    {
        var items = await CompleteAsync("SELECT id::| FROM users");

        Assert.DoesNotContain(items, i => i.Kind == "column");
    }

    [Theory]
    [InlineData(DbKind.Postgres, "text")]
    [InlineData(DbKind.Oracle, "VARCHAR2")]
    public async Task ВнутриCast_ПослеAs_ПредлагаютсяТипы(DbKind dialect, string expected)
    {
        var items = await CompleteAsync("SELECT CAST(id AS |) FROM users", dialect);

        Assert.Contains(items, i => i.Kind == "type" && i.Label == expected);
    }

    [Fact]
    public async Task AsВнеCast_ЭтоАлиас_ТипыНеПредлагаются()
    {
        // Регрессия: «SELECT id AS |» — пользователь придумывает имя колонки, а не тип.
        var items = await CompleteAsync("SELECT id AS | FROM users");

        Assert.DoesNotContain(items, i => i.Kind == "type");
    }

    [Fact]
    public async Task Oracle_ДвойноеДвоеточие_НеСчитаетсяПриведениемТипа()
    {
        // В PL/SQL «::» не оператор приведения — типы там предлагать не за что.
        var items = await CompleteAsync("SELECT id::| FROM users", DbKind.Oracle);

        Assert.DoesNotContain(items, i => i.Kind == "type");
    }

    // ================================================================== Oracle-специфика

    [Fact]
    public async Task Oracle_ПослеFrom_ПредлагаетсяDual()
    {
        var items = await CompleteAsync("SELECT SYSDATE FROM |", DbKind.Oracle);

        Assert.Contains(items, i => i.Label == "DUAL" && i.Kind == "table");
    }

    [Fact]
    public async Task Postgres_ПослеFrom_DualНеПредлагается()
    {
        var items = await CompleteAsync("SELECT 1 FROM |");

        Assert.DoesNotContain(items, i => i.Label == "DUAL");
    }

    [Fact]
    public async Task Oracle_НеизвестныйКвалификатор_ПредлагаетNextvalИCurrval()
    {
        // Последовательностей в снапшоте схемы нет, поэтому опознать её нечем.
        // Пустой список хуже двух верных вариантов.
        var items = await CompleteAsync("SELECT order_seq.| FROM DUAL", DbKind.Oracle);

        var labels = items.Select(i => i.Label).ToList();
        Assert.Contains("NEXTVAL", labels);
        Assert.Contains("CURRVAL", labels);
    }

    [Fact]
    public async Task Oracle_КвалификаторПакета_ПредлагаетЕгоПодпрограммы()
    {
        var items = await CompleteAsync("BEGIN DBMS_OUTPUT.| END;", DbKind.Oracle);

        var putLine = items.First(i => i.Label == "PUT_LINE");
        Assert.Equal("PUT_LINE(", putLine.InsertText);
        // Последовательность здесь ни при чём.
        Assert.DoesNotContain(items, i => i.Label == "NEXTVAL");
    }

    [Fact]
    public async Task Postgres_НеизвестныйКвалификатор_NextvalНеПредлагается()
    {
        var items = await CompleteAsync("SELECT order_seq.| FROM users");

        Assert.DoesNotContain(items, i => i.Label == "NEXTVAL");
    }

    // ================================================================== VALUES

    [Fact]
    public async Task ВValues_ПредлагаютсяЗначенияИФункции_НоНеКолонки()
    {
        var items = await CompleteAsync("INSERT INTO users (id, name) VALUES (|)");

        var labels = items.Select(i => i.Label).ToList();
        Assert.Contains("DEFAULT", labels);
        Assert.Contains("NULL", labels);
        Assert.Contains(items, i => i.Kind == "function");
        Assert.DoesNotContain(items, i => i.Kind == "column");
    }

    [Fact]
    public async Task ВSet_КолонкиОстаютсяИДобавляютсяФункции()
    {
        var items = await CompleteAsync("UPDATE users SET |");

        Assert.Contains(items, i => i.Kind == "column" && i.Label == "name");
        Assert.Contains(items, i => i.Kind == "function");
    }

    // ================================================================== История использования

    [Fact]
    public async Task НедавноИспользованнаяТаблица_ПоднимаетсяВыше()
    {
        // До обращения к orders обе таблицы равны по приоритету.
        var before = await CompleteAsync("SELECT * FROM |");
        var usersBefore = before.First(i => i.Label == "users");
        var ordersBefore = before.First(i => i.Label == "orders");
        Assert.Equal(usersBefore.SortPriority, ordersBefore.SortPriority);

        // Запрос по orders делает её «недавней» для этого датасорса.
        await CompleteAsync("SELECT total FROM orders WHERE |");

        var after = await CompleteAsync("SELECT * FROM |");
        var usersAfter = after.First(i => i.Label == "users");
        var ordersAfter = after.First(i => i.Label == "orders");
        Assert.True(ordersAfter.SortPriority < usersAfter.SortPriority);
    }
}
