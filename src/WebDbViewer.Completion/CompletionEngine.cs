using System.Text.RegularExpressions;
using WebDbViewer.Core;

namespace WebDbViewer.Completion;

/// <summary>
/// Движок автодополнения: antlr4-c3 по соответствующему диалекту + кэш метаданных +
/// семантическая модель v1 (области видимости FROM/JOIN/CTE/подзапросы, FK-сниппеты для ON,
/// INSERT INTO (…), ORDER/GROUP BY, автоалиасы, раскрытие t.*, humps-матчинг).
/// При сбое семантического анализа откатывается на v0-эвристику (FROM/JOIN → таблицы,
/// SELECT/WHERE/«алиас.» → колонки).
/// </summary>
public sealed partial class CompletionEngine : ICompletionEngine, ISemanticCompletionEngine
{
    // Базовые приоритеты сортировки (меньше = выше в списке).
    private const int PriorityContextBoost = 5;
    private const int PriorityColumn = 10;
    private const int PriorityTable = 20;
    private const int PriorityKeyword = 30;

    private static readonly HashSet<string> TableContextWords =
        new(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN", "INTO", "UPDATE", "TABLE" };

    private static readonly HashSet<string> ColumnContextWords =
        new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WHERE", "BY", "ON", "AND", "OR", "HAVING", "SET", "DISTINCT" };

    /// <summary>Слова, которые не могут быть алиасом таблицы (обрезают FROM-клаузу).</summary>
    private static readonly HashSet<string> NonAliasWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER",
        "GROUP", "ORDER", "SET", "VALUES", "USING", "LIMIT", "OFFSET", "HAVING", "AS",
        "UNION", "INTERSECT", "EXCEPT", "RETURNING", "FETCH", "FOR", "WINDOW", "SELECT", "START",
    };

    private readonly IMetadataCache _metadata;
    private readonly SemanticCompleter _semantic;
    private readonly object _cacheLock = new();

    // Кэш последнего результата: (хэш sql, каретка, диалект, датасорс, схема, опции) → items.
    private (long Key, IReadOnlyList<CompletionItem> Items)? _lastResult;

    public CompletionEngine(IMetadataCache metadata)
    {
        _metadata = metadata;
        _semantic = new SemanticCompleter(metadata);
    }

    public Task<IReadOnlyList<CompletionItem>> CompleteAsync(
        CompletionRequest request, DbKind dialect, CancellationToken ct) =>
        CompleteAsync(request, dialect, null, ct);

    /// <summary>Автодополнение с настройками v1 (автоалиасы и т.п.).</summary>
    public async Task<IReadOnlyList<CompletionItem>> CompleteAsync(
        CompletionRequest request, DbKind dialect, CompletionOptions? options, CancellationToken ct)
    {
        var opts = options ?? CompletionOptions.Default;
        var sql = request.SqlText ?? string.Empty;
        var caret = Math.Clamp(request.CaretOffset, 0, sql.Length);
        var prefix = sql[..caret];
        var limit = request.Limit > 0 ? request.Limit : 200;

        var cacheKey = ComputeCacheKey(sql, caret, dialect, request.DataSourceId, request.DefaultSchema, opts);
        lock (_cacheLock)
        {
            if (_lastResult is { } last && last.Key == cacheKey)
                return last.Items;
        }

        // ---------- 1. Грамматический анализ префикса (antlr4-c3); ошибки не фатальны.
        GrammarCandidates grammar;
        try
        {
            grammar = GrammarAnalyzer.Analyze(prefix, dialect);
        }
        catch
        {
            grammar = new GrammarCandidates { Keywords = new HashSet<string>() };
        }

        // ---------- 2. Контекст текста до каретки (используется и семантикой, и v0-эвристикой).
        var wordPrefix = ExtractWordPrefix(prefix);
        var beforeWord = prefix[..^wordPrefix.Length];
        var alias = ExtractAliasBeforeDot(beforeWord);
        var prevWord = ExtractPreviousWord(beforeWord);

        var snapshot = await TryGetSchemaSnapshotAsync(request, dialect, ct);

        // ---------- 3. Семантическая модель v1; при сбое/нераспознанном контексте — v0-эвристика.
        List<CompletionItem>? items = null;
        try
        {
            items = await _semantic.TryCompleteAsync(request, dialect, opts, snapshot, grammar.SuggestColumns, ct);
        }
        catch
        {
            items = null; // семантика не должна ломать базовое поведение
        }

        if (items is null)
        {
            var afterTableWord = prevWord is not null && TableContextWords.Contains(prevWord);
            var afterColumnWord = prevWord is not null && ColumnContextWords.Contains(prevWord);

            var suggestTables = (grammar.SuggestTables || afterTableWord) && alias is null;
            var suggestColumns = (grammar.SuggestColumns || afterColumnWord || alias is not null) && !afterTableWord;

            var tablePriority = afterTableWord ? PriorityContextBoost : PriorityTable;
            var columnPriority = afterColumnWord || alias is not null ? PriorityContextBoost : PriorityColumn;

            items = new List<CompletionItem>(128);

            if (suggestTables)
                await AddTablesAsync(items, request, snapshot, wordPrefix, tablePriority, dialect, ct);

            if (suggestColumns && snapshot is not null)
                AddColumns(items, snapshot, sql, alias, wordPrefix, columnPriority, dialect);
        }

        // ---------- 4. Ключевые слова из грамматики (при «алиас.» не имеют смысла).
        if (alias is null)
        {
            foreach (var kw in grammar.Keywords.OrderBy(k => k, StringComparer.Ordinal))
            {
                if (wordPrefix.Length > 0 && !kw.StartsWith(wordPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(new CompletionItem
                {
                    Label = kw,
                    Kind = "keyword",
                    InsertText = kw,
                    SortPriority = PriorityKeyword,
                });
            }
        }

        // ---------- 5. Дедупликация, сортировка, лимит.
        var result = items
            .GroupBy(i => (i.Kind, Label: i.Label.ToUpperInvariant()))
            .Select(g => g.OrderBy(i => i.SortPriority).First())
            .OrderBy(i => i.SortPriority)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        lock (_cacheLock)
        {
            _lastResult = (cacheKey, result);
        }
        return result;
    }

    // ================================================================== Метаданные

    private async Task<SchemaSnapshot?> TryGetSchemaSnapshotAsync(
        CompletionRequest request, DbKind dialect, CancellationToken ct)
    {
        var schema = request.DefaultSchema
                     ?? (dialect == DbKind.Postgres ? "public" : null);
        if (string.IsNullOrWhiteSpace(schema))
            return null;
        try
        {
            return await _metadata.GetSchemaAsync(request.DataSourceId, schema, ct);
        }
        catch
        {
            // Метаданные недоступны (нет соединения и т.п.) — автодополнение по ключевым словам.
            return null;
        }
    }

    private async Task AddTablesAsync(
        List<CompletionItem> items, CompletionRequest request, SchemaSnapshot? snapshot,
        string wordPrefix, int priority, DbKind dialect, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (snapshot is not null)
        {
            foreach (var table in snapshot.Tables)
            {
                if (wordPrefix.Length > 0 && !table.Name.StartsWith(wordPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(table.Schema + "." + table.Name))
                    continue;
                items.Add(MakeTableItem(table.Name, table.Schema, table.Type, table.Comment, priority, dialect));
            }
        }

        // Поиск по всем закэшированным схемам (кросс-схемные объекты).
        try
        {
            var found = await _metadata.SearchAsync(request.DataSourceId, wordPrefix, 100, ct);
            foreach (var node in found)
            {
                if (node.Type is not (DbObjectType.Table or DbObjectType.View or DbObjectType.MaterializedView))
                    continue;
                if (!seen.Add((node.Schema ?? "") + "." + node.Name))
                    continue;
                items.Add(MakeTableItem(node.Name, node.Schema, node.Type, node.Comment, priority, dialect));
            }
        }
        catch
        {
            // Поиск опционален.
        }
    }

    private static CompletionItem MakeTableItem(
        string name, string? schema, DbObjectType type, string? comment, int priority, DbKind dialect) => new()
    {
        Label = name,
        Kind = type == DbObjectType.Table ? "table" : "view",
        InsertText = SqlIdentifierQuoting.Quote(name, dialect),
        Detail = schema,
        Documentation = comment,
        SortPriority = priority,
    };

    private static void AddColumns(
        List<CompletionItem> items, SchemaSnapshot snapshot, string fullSql,
        string? alias, string wordPrefix, int priority, DbKind dialect)
    {
        // Regex-проход по FROM/JOIN/UPDATE/INTO всего текста: алиас → таблица.
        var aliasMap = BuildAliasMap(fullSql);

        IEnumerable<TableInfo> sourceTables;
        if (alias is not null)
        {
            // «алиас.» — только колонки его таблицы.
            if (!aliasMap.TryGetValue(alias, out var tableName))
                return;
            var table = FindTable(snapshot, tableName);
            if (table is null)
                return;
            sourceTables = [table];
        }
        else
        {
            // Колонки таблиц, упомянутых в запросе; если ничего не распознали — всех таблиц схемы.
            var referenced = aliasMap.Values
                .Select(t => FindTable(snapshot, t))
                .Where(t => t is not null)
                .Select(t => t!)
                .Distinct()
                .ToList();
            sourceTables = referenced.Count > 0 ? referenced : snapshot.Tables;
        }

        foreach (var table in sourceTables)
        {
            foreach (var column in table.Columns)
            {
                if (wordPrefix.Length > 0 && !column.Name.StartsWith(wordPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(new CompletionItem
                {
                    Label = column.Name,
                    Kind = "column",
                    InsertText = SqlIdentifierQuoting.Quote(column.Name, dialect),
                    Detail = $"{table.Name}.{column.Name} : {column.DataType}",
                    Documentation = column.Comment,
                    SortPriority = priority,
                });
            }
        }
    }

    private static TableInfo? FindTable(SchemaSnapshot snapshot, string name)
    {
        var clean = name.Trim('"');
        var dotIndex = clean.LastIndexOf('.');
        if (dotIndex >= 0)
            clean = clean[(dotIndex + 1)..].Trim('"');
        return snapshot.Tables.FirstOrDefault(
            t => string.Equals(t.Name, clean, StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================== Разбор контекста

    [GeneratedRegex(@"(?:\bfrom\b|\bjoin\b|\bupdate\b|\binto\b)\s+(""[^""]+""|[\w$#.]+)(?:\s+(?:as\s+)?([A-Za-z_][\w$#]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex FromClauseRegex();

    /// <summary>Простая карта «алиас/имя таблицы → имя таблицы» по FROM/JOIN/UPDATE/INTO.</summary>
    internal static Dictionary<string, string> BuildAliasMap(string sql)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FromClauseRegex().Matches(sql))
        {
            var table = m.Groups[1].Value;
            var alias = m.Groups[2].Success ? m.Groups[2].Value : null;
            if (alias is not null && !NonAliasWords.Contains(alias))
                map[alias] = table;
            // Само имя таблицы тоже работает как квалификатор (users.id).
            var bare = table.Trim('"');
            var dot = bare.LastIndexOf('.');
            if (dot >= 0)
                bare = bare[(dot + 1)..].Trim('"');
            if (bare.Length > 0 && !map.ContainsKey(bare))
                map[bare] = table;
        }
        return map;
    }

    /// <summary>Начатое слово непосредственно перед кареткой (для фильтрации кандидатов).</summary>
    private static string ExtractWordPrefix(string prefix)
    {
        var i = prefix.Length;
        while (i > 0 && (char.IsLetterOrDigit(prefix[i - 1]) || prefix[i - 1] is '_' or '$' or '#'))
            i--;
        return prefix[i..];
    }

    [GeneratedRegex(@"([A-Za-z_][\w$#]*)\s*\.\s*$")]
    private static partial Regex AliasBeforeDotRegex();

    /// <summary>«алиас.» перед кареткой (уже без начатого слова) → имя алиаса, иначе null.</summary>
    private static string? ExtractAliasBeforeDot(string beforeWord)
    {
        var m = AliasBeforeDotRegex().Match(beforeWord);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"([A-Za-z_][\w$#]*)\s*$")]
    private static partial Regex PreviousWordRegex();

    /// <summary>Предыдущее значимое слово перед кареткой (без начатого слова), UPPER.</summary>
    private static string? ExtractPreviousWord(string beforeWord)
    {
        var m = PreviousWordRegex().Match(beforeWord);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static long ComputeCacheKey(
        string sql, int caret, DbKind dialect, Guid dsId, string? schema, CompletionOptions opts)
    {
        var h1 = (long)string.GetHashCode(sql, StringComparison.Ordinal);
        var h2 = (long)HashCode.Combine(caret, dialect, dsId, schema, opts.AutoAliasTables);
        return (h1 << 32) ^ (uint)h2;
    }
}
