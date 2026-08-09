using WebDbViewer.Completion;
using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Completion;

/// <summary>
/// Функции и процедуры схемы в подсказках. Фейковый кэш содержит функцию calc_total
/// и процедуру archive_orders в схеме public.
/// </summary>
public class RoutineCompletionTests
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
        }, dialect, CancellationToken.None);
    }

    [Fact]
    public async Task ВSelect_ПредлагаютсяФункцииИПроцедурыСхемы()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        var routines = items.Where(i => i.Kind == "function").Select(i => i.Label).ToList();
        Assert.Contains("calc_total", routines);
        Assert.Contains("archive_orders", routines);
    }

    [Fact]
    public async Task ВWhere_ПредлагаютсяФункции()
    {
        var items = await CompleteAsync("SELECT * FROM users WHERE |");

        Assert.Contains(items, i => i.Kind == "function" && i.Label == "calc_total");
    }

    [Fact]
    public async Task ВставкаФункции_ОткрываетСкобку()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        var function = items.First(i => i.Kind == "function" && i.Label == "calc_total");
        Assert.Equal("calc_total(", function.InsertText);
    }

    [Fact]
    public async Task ПодсказкаФункции_ПоказываетСигнатуруИКомментарий()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        var function = items.First(i => i.Kind == "function" && i.Label == "calc_total");
        Assert.Equal("public.calc_total(order_id bigint)", function.Detail);
        Assert.Contains("Сумма заказа", function.Documentation);
        Assert.Contains("numeric", function.Documentation);
    }

    [Fact]
    public async Task ПроцедураБезТипаВозврата_НеУпоминаетВозвращаемоеЗначение()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        var procedure = items.First(i => i.Kind == "function" && i.Label == "archive_orders");
        Assert.Equal("public.archive_orders(before date)", procedure.Detail);
        Assert.True(string.IsNullOrEmpty(procedure.Documentation));
    }

    [Fact]
    public async Task ПослеFrom_ФункцииНеПредлагаются()
    {
        var items = await CompleteAsync("SELECT * FROM |");

        Assert.DoesNotContain(items, i => i.Kind == "function");
    }

    [Fact]
    public async Task НачатоеИмяФункции_ФильтруетСписок()
    {
        var items = await CompleteAsync("SELECT calc| FROM users");

        var routines = items.Where(i => i.Kind == "function").Select(i => i.Label).ToList();
        Assert.Contains("calc_total", routines);
        Assert.DoesNotContain("archive_orders", routines);
    }

    [Fact]
    public async Task ИмяСхемыСТочкой_ПредлагаетФункцииСхемы()
    {
        var items = await CompleteAsync("SELECT public.| FROM users");

        Assert.Contains(items, i => i.Kind == "function" && i.Label == "calc_total");
    }

    [Fact]
    public async Task Oracle_ФункцииПредлагаютсяТакЖе()
    {
        var items = await CompleteAsync("SELECT | FROM users", DbKind.Oracle);

        Assert.Contains(items, i => i.Kind == "function" && i.Label == "calc_total");
    }

    [Fact]
    public async Task Функции_НижеКолонокНоВышеКлючевыхСлов()
    {
        var items = await CompleteAsync("SELECT | FROM users");

        var column = items.First(i => i.Kind == "column");
        var function = items.First(i => i.Kind == "function");
        var keyword = items.First(i => i.Kind == "keyword");

        Assert.True(column.SortPriority < function.SortPriority);
        Assert.True(function.SortPriority < keyword.SortPriority);
    }
}
