using WebDbViewer.Completion;
using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Completion;

/// <summary>
/// Корпус тестов автодополнения: маркер «|» — позиция каретки.
/// Метаданные — фейковый кэш (public: users, orders).
/// </summary>
public class CompletionEngineTests
{
    private readonly CompletionEngine _engine = new(new FakeMetadataCache());

    /// <summary>Выполняет автодополнение для текста с маркером каретки «|».</summary>
    private async Task<IReadOnlyList<CompletionItem>> CompleteAsync(string sqlWithCaret, DbKind dialect = DbKind.Postgres)
    {
        var caret = sqlWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "В тестовом SQL нет маркера каретки «|»");
        var sql = sqlWithCaret.Remove(caret, 1);

        return await _engine.CompleteAsync(new CompletionRequest
        {
            DataSourceId = FakeMetadataCache.DsId,
            SqlText = sql,
            CaretOffset = caret,
        }, dialect, CancellationToken.None);
    }

    [Fact]
    public async Task ПослеSelect_ПредлагаютсяКолонкиИКлючевыеСлова()
    {
        var items = await CompleteAsync("SELECT | FROM t");

        Assert.Contains(items, i => i.Kind == "column");
        Assert.Contains(items, i => i.Kind == "keyword");
        // Колонки выше ключевых слов (меньший SortPriority).
        var column = items.First(i => i.Kind == "column");
        var keyword = items.First(i => i.Kind == "keyword");
        Assert.True(column.SortPriority < keyword.SortPriority);
    }

    [Fact]
    public async Task ПослеFrom_ПредлагаютсяТаблицыИзКэша()
    {
        var items = await CompleteAsync("SELECT * FROM |");

        var tables = items.Where(i => i.Kind is "table" or "view").Select(i => i.Label).ToList();
        Assert.Contains("users", tables);
        Assert.Contains("orders", tables);
    }

    [Fact]
    public async Task АлиасСТочкой_ПредлагаютсяКолонкиТаблицыАлиаса()
    {
        var items = await CompleteAsync("SELECT a.| FROM users a");

        var columns = items.Where(i => i.Kind == "column").Select(i => i.Label).ToList();
        Assert.Contains("id", columns);
        Assert.Contains("name", columns);
        Assert.Contains("email", columns);
        // Колонок orders быть не должно.
        Assert.DoesNotContain("user_id", columns);
        Assert.DoesNotContain("total", columns);
    }

    [Fact]
    public async Task ПослеFromСПрефиксом_ФильтруютсяТаблицыПоПрефиксу()
    {
        var items = await CompleteAsync("SELECT id FROM u|");

        var tables = items.Where(i => i.Kind is "table" or "view").Select(i => i.Label).ToList();
        Assert.Contains("users", tables);
        Assert.DoesNotContain("orders", tables);
    }

    [Fact]
    public async Task ПослеJoin_ПредлагаютсяТаблицы()
    {
        var items = await CompleteAsync("SELECT * FROM users u JOIN |");

        Assert.Contains(items, i => i.Kind is "table" or "view" && i.Label == "orders");
    }

    [Fact]
    public async Task ПослеWhere_ПредлагаютсяКолонкиУпомянутойТаблицы()
    {
        var items = await CompleteAsync("SELECT * FROM orders WHERE |");

        var columns = items.Where(i => i.Kind == "column").Select(i => i.Label).ToList();
        Assert.Contains("user_id", columns);
        Assert.Contains("total", columns);
    }

    [Fact]
    public async Task ИмяТаблицыКакКвалификатор_РаботаетКакАлиас()
    {
        var items = await CompleteAsync("SELECT users.| FROM users");

        var columns = items.Where(i => i.Kind == "column").Select(i => i.Label).ToList();
        Assert.Contains("email", columns);
        Assert.DoesNotContain("total", columns);
    }

    [Fact]
    public async Task Лимит_НеПревышается()
    {
        var items = await _engine.CompleteAsync(new CompletionRequest
        {
            DataSourceId = FakeMetadataCache.DsId,
            SqlText = "SELECT ",
            CaretOffset = 7,
            Limit = 10,
        }, DbKind.Postgres, CancellationToken.None);

        Assert.True(items.Count <= 10);
    }

    [Fact]
    public async Task ПовторныйЗапрос_ВозвращаетКэшированныйРезультат()
    {
        var first = await CompleteAsync("SELECT * FROM |");
        var second = await CompleteAsync("SELECT * FROM |");

        Assert.Same(first, second);
    }

    [Fact]
    public async Task КвотированиеИдентификаторов_ВInsertText()
    {
        // «Обычные» PG-идентификаторы (lowercase) не квотируются.
        var items = await CompleteAsync("SELECT * FROM u|");

        var users = items.First(i => i.Kind is "table" or "view" && i.Label == "users");
        Assert.Equal("users", users.InsertText);
    }

    [Fact]
    public async Task Дедупликация_НетПовторовКандидатов()
    {
        var items = await CompleteAsync("SELECT * FROM |");

        var duplicates = items
            .GroupBy(i => (i.Kind, i.Label.ToUpperInvariant()))
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.Empty(duplicates);
    }
}
