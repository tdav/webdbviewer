using Antlr4.Runtime;
using Antlr4CodeCompletion.Core.CodeCompletion;
using WebDbViewer.Core;
using WebDbViewer.Parsing.PlSql;
using WebDbViewer.Parsing.Postgres;

namespace WebDbViewer.Completion;

/// <summary>Результат грамматического анализа позиции каретки (antlr4-c3).</summary>
internal sealed record GrammarCandidates
{
    /// <summary>Допустимые в позиции каретки ключевые слова (нормализованный UPPER-вид).</summary>
    public required IReadOnlySet<string> Keywords { get; init; }
    /// <summary>Каретка в позиции имени таблицы/отношения.</summary>
    public bool SuggestTables { get; init; }
    /// <summary>Каретка в позиции ссылки на колонку.</summary>
    public bool SuggestColumns { get; init; }
}

/// <summary>
/// Анализ префикса SQL (текст до каретки) через ANTLR-парсер + antlr4-c3:
/// какие ключевые слова и категории объектов допустимы в позиции каретки.
/// </summary>
internal static class GrammarAnalyzer
{
    // ---- PostgreSQL: номера preferred-правил (см. PostgreSQLParser.RULE_*).
    private static readonly HashSet<int> PgPreferredRules =
    [
        PostgreSQLParser.RULE_relation_expr,   // имя таблицы во FROM/UPDATE/DELETE
        PostgreSQLParser.RULE_qualified_name,  // имя таблицы в INSERT INTO и пр.
        PostgreSQLParser.RULE_columnref,       // ссылка на колонку в выражениях
    ];

    // ---- Oracle PL/SQL.
    private static readonly HashSet<int> PlSqlPreferredRules =
    [
        PlSqlParser.RULE_tableview_name,
        PlSqlParser.RULE_column_name,
        PlSqlParser.RULE_general_element,      // выражения вида a.b — колонки
    ];

    private static readonly Lazy<HashSet<int>> PgIgnoredTokens = new(BuildPgIgnoredTokens);
    private static readonly Lazy<HashSet<int>> PlSqlIgnoredTokens = new(BuildPlSqlIgnoredTokens);

    /// <summary>Анализирует префикс SQL до каретки. Исключения глотать не должен — ловит вызывающий.</summary>
    public static GrammarCandidates Analyze(string sqlPrefix, DbKind dialect) =>
        dialect == DbKind.Oracle ? AnalyzePlSql(sqlPrefix) : AnalyzePostgres(sqlPrefix);

    // ================================================================== PostgreSQL

    private static GrammarCandidates AnalyzePostgres(string prefix)
    {
        var lexer = new PostgreSQLLexer(CharStreams.fromString(prefix));
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        var parser = new PostgreSQLParser(tokens);
        parser.RemoveErrorListeners();
        parser.root(); // восстановление после ошибок включено — префикс почти всегда «незакончен»

        var caretIndex = FindCaretTokenIndex(tokens, prefix.Length);
        var core = new CodeCompletionCore(parser, PgPreferredRules, PgIgnoredTokens.Value);
        var candidates = core.CollectCandidates(caretIndex, null!);

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tokenType in candidates.Tokens.Keys)
        {
            var name = NormalizeKeyword(parser.Vocabulary.GetSymbolicName(tokenType));
            if (name is not null)
                keywords.Add(name);
        }

        return new GrammarCandidates
        {
            Keywords = keywords,
            SuggestTables = candidates.Rules.ContainsKey(PostgreSQLParser.RULE_relation_expr)
                            || candidates.Rules.ContainsKey(PostgreSQLParser.RULE_qualified_name),
            SuggestColumns = candidates.Rules.ContainsKey(PostgreSQLParser.RULE_columnref),
        };
    }

    private static HashSet<int> BuildPgIgnoredTokens()
    {
        // 1..29 — пунктуация/операторы; от Identifier и дальше — идентификаторы, литералы, trivia.
        var ignored = new HashSet<int>();
        for (var t = PostgreSQLLexer.Dollar; t <= PostgreSQLLexer.Operator; t++)
            ignored.Add(t);
        for (var t = PostgreSQLLexer.Identifier; t <= PostgreSQLLexer.AfterEscapeStringConstantWithNewlineMode_Continued; t++)
            ignored.Add(t);
        return ignored;
    }

    // ================================================================== Oracle PL/SQL

    private static GrammarCandidates AnalyzePlSql(string prefix)
    {
        var lexer = new PlSqlLexer(CharStreams.fromString(prefix));
        lexer.RemoveErrorListeners();
        var tokens = new CommonTokenStream(lexer);
        var parser = new PlSqlParser(tokens);
        parser.RemoveErrorListeners();
        parser.sql_script();

        var caretIndex = FindCaretTokenIndex(tokens, prefix.Length);
        var core = new CodeCompletionCore(parser, PlSqlPreferredRules, PlSqlIgnoredTokens.Value);
        var candidates = core.CollectCandidates(caretIndex, null!);

        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tokenType in candidates.Tokens.Keys)
        {
            var name = NormalizeKeyword(parser.Vocabulary.GetSymbolicName(tokenType));
            if (name is not null)
                keywords.Add(name);
        }

        return new GrammarCandidates
        {
            Keywords = keywords,
            SuggestTables = candidates.Rules.ContainsKey(PlSqlParser.RULE_tableview_name),
            SuggestColumns = candidates.Rules.ContainsKey(PlSqlParser.RULE_column_name)
                             || candidates.Rules.ContainsKey(PlSqlParser.RULE_general_element),
        };
    }

    private static HashSet<int> BuildPlSqlIgnoredTokens()
    {
        // Литералы, пунктуация, операторы, комментарии, идентификаторы — от NATIONAL_CHAR_STRING_LIT до SPACES.
        var ignored = new HashSet<int>();
        for (var t = PlSqlLexer.NATIONAL_CHAR_STRING_LIT; t <= PlSqlLexer.SPACES; t++)
            ignored.Add(t);
        return ignored;
    }

    // ================================================================== Общее

    /// <summary>
    /// Индекс токена каретки в потоке. Префикс заканчивается ровно на каретке, поэтому:
    /// каретка внутри/сразу после слова — индекс этого токена, иначе — индекс EOF.
    /// </summary>
    private static int FindCaretTokenIndex(CommonTokenStream tokens, int caretOffset)
    {
        tokens.Fill();
        var all = tokens.GetTokens();
        var eofIndex = all.Count - 1;

        // Если каретка примыкает к последнему значимому токену-слову — дополняем это слово.
        for (var k = all.Count - 1; k >= 0; k--)
        {
            var t = all[k];
            if (t.Type == TokenConstants.EOF || t.Channel != TokenConstants.DefaultChannel)
                continue;
            if (t.StartIndex <= caretOffset - 1 && t.StopIndex >= caretOffset - 1
                && t.Text is { Length: > 0 } text && (char.IsLetterOrDigit(text[^1]) || text[^1] == '_'))
                return t.TokenIndex;
            break;
        }
        return eofIndex;
    }

    /// <summary>SELECT_P → SELECT; отбрасывает служебные имена (не «слово-ключ»).</summary>
    private static string? NormalizeKeyword(string? symbolicName)
    {
        if (string.IsNullOrEmpty(symbolicName))
            return null;
        var name = symbolicName.EndsWith("_P", StringComparison.Ordinal)
            ? symbolicName[..^2]
            : symbolicName;
        if (name.Length < 2)
            return null;
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterUpper(c) && c != '_' && !char.IsAsciiDigit(c))
                return null;
        }
        return name;
    }
}
