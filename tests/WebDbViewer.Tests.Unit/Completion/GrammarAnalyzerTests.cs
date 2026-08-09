using WebDbViewer.Completion;
using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Completion;

/// <summary>
/// Грамматический анализ позиции каретки: обрезка до текущего statement, кэш результатов,
/// резервный словарь ключевых слов диалекта.
/// </summary>
public class GrammarAnalyzerTests
{
    // ================================================================== Обрезка до statement'а

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void ОбрезкаStatement_ОтбрасываетПредыдущиеЗапросы(DbKind dialect)
    {
        const string sql = "SELECT * FROM users;\nSELECT name FROM orders";

        var prefix = GrammarAnalyzer.TrimToCurrentStatement(sql, sql.Length, dialect);

        Assert.Equal("SELECT name FROM orders", prefix);
    }

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void ОбрезкаStatement_КареткаСразуПослеТочкиСЗапятой_ДаётПустойПрефикс(DbKind dialect)
    {
        const string sql = "SELECT * FROM users; ";

        var prefix = GrammarAnalyzer.TrimToCurrentStatement(sql, sql.Length, dialect);

        Assert.Equal(string.Empty, prefix);
    }

    [Fact]
    public void ОбрезкаStatement_КареткаВнутриПервогоЗапроса_ВторойНеПопадает()
    {
        const string sql = "SELECT id FROM users; SELECT * FROM orders";
        var caret = sql.IndexOf(" FROM users", StringComparison.Ordinal);

        var prefix = GrammarAnalyzer.TrimToCurrentStatement(sql, caret, DbKind.Postgres);

        Assert.Equal("SELECT id", prefix);
    }

    [Fact]
    public void ОбрезкаStatement_БлочныйКомментарийВНачале_НеПортится()
    {
        // Ведущий «/» отбрасывается как терминатор Oracle-блока, но «/*» — начало комментария.
        const string sql = "/* заметка */ SELECT id FROM users";

        var prefix = GrammarAnalyzer.TrimToCurrentStatement(sql, sql.Length, DbKind.Oracle);

        Assert.Equal(sql, prefix);
    }

    [Fact]
    public void ОбрезкаStatement_КареткаВНуле_ДаётПустойПрефикс()
    {
        Assert.Equal(string.Empty, GrammarAnalyzer.TrimToCurrentStatement("SELECT 1", 0, DbKind.Postgres));
    }

    // ================================================================== Кэш результатов

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void ПовторныйАнализ_ДаётТотЖеНаборКандидатов(DbKind dialect)
    {
        const string sql = "SELECT * FROM ";

        var first = GrammarAnalyzer.Analyze(sql, sql.Length, dialect);
        var second = GrammarAnalyzer.Analyze(sql, sql.Length, dialect);

        Assert.Equal(first.SuggestTables, second.SuggestTables);
        Assert.Equal(first.SuggestColumns, second.SuggestColumns);
        Assert.Equal(first.Keywords.OrderBy(k => k, StringComparer.Ordinal),
                     second.Keywords.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Анализ_ОдинаковыйStatementПослеРазныхПредыдущих_ДаётОдинаковыйРезультат()
    {
        // Прямое следствие обрезки: контекст каретки определяется только текущим statement'ом.
        const string alone = "SELECT * FROM ";
        const string after = "INSERT INTO orders VALUES (1);\nSELECT * FROM ";

        var first = GrammarAnalyzer.Analyze(alone, alone.Length, DbKind.Postgres);
        var second = GrammarAnalyzer.Analyze(after, after.Length, DbKind.Postgres);

        Assert.Equal(first.SuggestTables, second.SuggestTables);
        Assert.Equal(first.Keywords.OrderBy(k => k, StringComparer.Ordinal),
                     second.Keywords.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Анализ_ПозицияТаблицы_РаспознаётсяВОбоихДиалектах()
    {
        const string sql = "SELECT * FROM ";

        Assert.True(GrammarAnalyzer.Analyze(sql, sql.Length, DbKind.Postgres).SuggestTables);
        Assert.True(GrammarAnalyzer.Analyze(sql, sql.Length, DbKind.Oracle).SuggestTables);
    }

    // ================================================================== Словарь диалекта

    [Theory]
    [InlineData(DbKind.Postgres)]
    [InlineData(DbKind.Oracle)]
    public void СловарьКлючевыхСлов_СодержитБазовыеКомандыДиалекта(DbKind dialect)
    {
        var keywords = GrammarAnalyzer.DialectKeywords(dialect);

        Assert.Contains("SELECT", keywords);
        Assert.Contains("FROM", keywords);
        Assert.Contains("WHERE", keywords);
        Assert.Contains("INSERT", keywords);
        Assert.Contains("UPDATE", keywords);
    }

    [Fact]
    public void СловарьКлючевыхСлов_СодержитСпецифичныеДляДиалекта()
    {
        Assert.Contains("CONNECT", GrammarAnalyzer.DialectKeywords(DbKind.Oracle));
        Assert.Contains("RETURNING", GrammarAnalyzer.DialectKeywords(DbKind.Postgres));
    }

    [Fact]
    public void СловарьКлючевыхСлов_НеСодержитТокеновИдентификаторовИЛитералов()
    {
        // Именно имена токенов, а не слова: в PL/SQL «IDENTIFIER» — ключевое слово Oracle
        // (токен 686), а идентификатор — REGULAR_ID.
        var oracle = GrammarAnalyzer.DialectKeywords(DbKind.Oracle);
        Assert.DoesNotContain("REGULAR_ID", oracle);
        Assert.DoesNotContain("DELIMITED_ID", oracle);
        Assert.DoesNotContain("CHAR_STRING", oracle);

        var postgres = GrammarAnalyzer.DialectKeywords(DbKind.Postgres);
        Assert.DoesNotContain("Identifier", postgres);
        Assert.DoesNotContain("QuotedIdentifier", postgres);
    }
}
