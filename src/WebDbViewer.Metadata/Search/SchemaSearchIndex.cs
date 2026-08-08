using WebDbViewer.Core;

namespace WebDbViewer.Metadata.Search;

/// <summary>Найденный объект с оценкой релевантности.</summary>
public sealed record ScoredNode(DbObjectNode Node, int Score);

/// <summary>
/// Поисковый индекс одной схемы: префиксный trie + humps/substring-матчинг.
/// Ранжирование: точный префикс &gt; humps &gt; substring; таблицы с FK выше.
/// Индекс неизменяем после построения (пересоздаётся при обновлении снапшота).
/// </summary>
public sealed class SchemaSearchIndex
{
    // Базовые веса категорий совпадения
    private const int PrefixScore = 3000;
    private const int ExactBonus = 500;
    private const int HumpsScore = 2000;
    private const int SubstringScore = 1000;
    private const int ForeignKeyBonus = 200;

    /// <summary>Элемент индекса: оригинальное имя сохраняется, сравнение — без учёта регистра.</summary>
    private sealed record SearchEntry(
        string Name,
        string Lower,
        DbObjectType Type,
        string Schema,
        string? Comment,
        bool HasForeignKeys,
        string? TableName);

    private readonly List<SearchEntry> _entries;
    private readonly PrefixTrie<SearchEntry> _trie;

    private SchemaSearchIndex(List<SearchEntry> entries, PrefixTrie<SearchEntry> trie)
    {
        _entries = entries;
        _trie = trie;
    }

    /// <summary>Строит индекс по снапшоту: таблицы/представления, их колонки и роутины.</summary>
    public static SchemaSearchIndex Build(SchemaSnapshot snapshot)
    {
        var entries = new List<SearchEntry>();
        foreach (var table in snapshot.Tables)
        {
            var hasFk = table.ForeignKeys.Count > 0;
            entries.Add(new SearchEntry(table.Name, table.Name.ToLowerInvariant(), table.Type, table.Schema, table.Comment, hasFk, null));
            foreach (var column in table.Columns)
                entries.Add(new SearchEntry(column.Name, column.Name.ToLowerInvariant(), DbObjectType.Column, table.Schema, column.Comment, false, table.Name));
        }
        foreach (var routine in snapshot.Routines)
            entries.Add(new SearchEntry(routine.Name, routine.Name.ToLowerInvariant(), routine.Type, routine.Schema, routine.Comment, false, null));

        var trie = new PrefixTrie<SearchEntry>();
        foreach (var entry in entries)
            trie.Add(entry.Lower, entry);

        return new SchemaSearchIndex(entries, trie);
    }

    /// <summary>Ищет объекты по запросу и добавляет результаты с оценками в аккумулятор.</summary>
    public void Search(string query, List<ScoredNode> accumulator)
    {
        if (string.IsNullOrWhiteSpace(query))
            return;

        var q = query.Trim().ToLowerInvariant();

        // Префиксные совпадения — через trie
        var prefixMatches = new HashSet<SearchEntry>(_trie.GetByPrefix(q));

        foreach (var entry in _entries)
        {
            int baseScore;
            if (prefixMatches.Contains(entry))
                baseScore = PrefixScore + (entry.Lower == q ? ExactBonus : 0);
            else if (HumpsMatcher.IsMatch(q, entry.Name))
                baseScore = HumpsScore;
            else if (entry.Lower.Contains(q, StringComparison.Ordinal))
                baseScore = SubstringScore;
            else
                continue;

            var score = baseScore
                        + KindBonus(entry.Type)
                        + (entry.HasForeignKeys ? ForeignKeyBonus : 0)
                        - entry.Name.Length; // более короткие имена — выше
            accumulator.Add(new ScoredNode(ToNode(entry), score));
        }
    }

    /// <summary>Приоритет по типу объекта: таблицы выше представлений, роутин и колонок.</summary>
    private static int KindBonus(DbObjectType type) => type switch
    {
        DbObjectType.Table => 100,
        DbObjectType.View or DbObjectType.MaterializedView => 80,
        DbObjectType.Function or DbObjectType.Procedure => 50,
        _ => 0
    };

    private static DbObjectNode ToNode(SearchEntry entry) => new()
    {
        Name = entry.Name,
        Type = entry.Type,
        Schema = entry.Schema,
        Comment = entry.Comment,
        HasChildren = entry.Type is DbObjectType.Table or DbObjectType.View or DbObjectType.MaterializedView,
        Attributes = entry.TableName is null
            ? null
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["table"] = entry.TableName }
    };
}
