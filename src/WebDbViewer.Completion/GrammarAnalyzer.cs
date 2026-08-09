using Antlr4.Runtime;
using Antlr4CodeCompletion.Core.CodeCompletion;
using WebDbViewer.Core;
using WebDbViewer.Parsing;
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

    // Полные словари ключевых слов диалектов из Vocabulary парсера — строятся один раз на процесс.
    // Нужны как источник подсказок, когда antlr4-c3 не дал ни одного кандидата.
    private static readonly Lazy<IReadOnlySet<string>> PgKeywords = new(
        () => BuildVocabularyKeywords(PostgreSQLParser.DefaultVocabulary, PgIgnoredTokens.Value));

    private static readonly Lazy<IReadOnlySet<string>> PlSqlKeywords = new(
        () => BuildVocabularyKeywords(PlSqlParser.DefaultVocabulary, PlSqlIgnoredTokens.Value));

    private static readonly StatementSplitter Splitter = new();

    // Кэш результатов antlr4-c3: ключ (диалект, текст statement'а до каретки) → кандидаты.
    // Результат зависит только от ключа, поэтому кэш общий для процесса.
    // ponytail: при переполнении словарь чистится целиком, а не вытесняет LRU-хвост —
    // прогретость восстанавливается за пару нажатий; ставить LRU, если профилировщик покажет промахи.
    private const int CacheCapacity = 256;
    private static readonly Dictionary<(DbKind Dialect, string Prefix), GrammarCandidates> Cache = new();
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// Анализирует текст до каретки. Разбирается только statement, внутри которого стоит каретка:
    /// стоимость перестаёт зависеть от длины скрипта, а синтаксис предыдущих запросов не влияет
    /// на восстановление после ошибок. Исключения глотать не должен — ловит вызывающий.
    /// </summary>
    public static GrammarCandidates Analyze(string sql, int caret, DbKind dialect)
    {
        var prefix = TrimToCurrentStatement(sql, caret, dialect);
        var key = (dialect, prefix);

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
        }

        var result = dialect == DbKind.Oracle ? AnalyzePlSql(prefix) : AnalyzePostgres(prefix);

        lock (CacheLock)
        {
            if (Cache.Count >= CacheCapacity)
                Cache.Clear();
            Cache[key] = result;
        }
        return result;
    }

    /// <summary>Все ключевые слова диалекта — резерв, когда antlr4-c3 не дал кандидатов.</summary>
    public static IReadOnlySet<string> DialectKeywords(DbKind dialect) =>
        dialect == DbKind.Oracle ? PlSqlKeywords.Value : PgKeywords.Value;

    /// <summary>
    /// Отрезает от текста всё, что предшествует statement'у с кареткой. Если каретка стоит уже
    /// за завершённым statement'ом (после «;» или строки с «/»), возвращается только хвост.
    /// </summary>
    internal static string TrimToCurrentStatement(string sql, int caret, DbKind dialect)
    {
        caret = Math.Clamp(caret, 0, sql.Length);
        if (caret == 0)
            return string.Empty;

        var start = 0;
        try
        {
            foreach (var statement in Splitter.Split(sql, dialect))
            {
                if (statement.Offset > caret)
                    break;
                var end = statement.Offset + statement.Text.Length;
                if (caret <= end)
                {
                    start = statement.Offset;
                    break;
                }
                // Каретка за концом statement'а. Сплиттер обрезает хвостовые пробелы, поэтому
                // «за концом» ещё не значит «в следующем»: переходим только через терминатор.
                start = HasTerminator(sql, end, caret) ? end : statement.Offset;
            }
        }
        catch
        {
            return sql[..caret]; // сплиттер не должен ломать анализ
        }

        // За концом завершённого statement'а остаются терминаторы; «/*» — начало комментария, не терминатор.
        while (start < caret)
        {
            var c = sql[start];
            if (char.IsWhiteSpace(c) || IsTerminator(sql, start, caret))
                start++;
            else
                break;
        }
        return sql[start..caret];
    }

    /// <summary>Разделяет ли участок [from, to) два statement'а, а не просто отступ.</summary>
    private static bool HasTerminator(string sql, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (IsTerminator(sql, i, to))
                return true;
        }
        return false;
    }

    /// <summary>«;» либо слэш-терминатор Oracle. «/*» — начало комментария, а не терминатор.</summary>
    private static bool IsTerminator(string sql, int index, int end) =>
        sql[index] == ';' || (sql[index] == '/' && (index + 1 >= end || sql[index + 1] != '*'));

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

    /// <summary>Ключевые слова из словаря парсера: всё, кроме литералов, пунктуации и идентификаторов.</summary>
    private static IReadOnlySet<string> BuildVocabularyKeywords(IVocabulary vocabulary, HashSet<int> ignored)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Верхнюю границу типов токенов знает Vocabulary, а не интерфейс IVocabulary; генератор
        // ANTLR всегда кладёт в DefaultVocabulary именно его. Если это изменится, резервный
        // словарь останется пустым — поведение не хуже, чем до его появления.
        if (vocabulary is not Vocabulary concrete)
            return keywords;

        for (var tokenType = 1; tokenType <= concrete.getMaxTokenType(); tokenType++)
        {
            if (ignored.Contains(tokenType))
                continue;
            var name = NormalizeKeyword(vocabulary.GetSymbolicName(tokenType));
            if (name is not null)
                keywords.Add(name);
        }
        return keywords;
    }

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
