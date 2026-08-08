namespace WebDbViewer.Completion.Semantics;

/// <summary>Ссылка на таблицу/CTE/подзапрос во FROM/JOIN (или цель INSERT/UPDATE).</summary>
internal sealed record TableRef
{
    /// <summary>Явно указанная схема (schema.table) или null.</summary>
    public string? Schema { get; init; }

    public required string Name { get; init; }

    public string? Alias { get; init; }

    /// <summary>Ссылка на CTE текущего запроса.</summary>
    public bool IsCte { get; init; }

    /// <summary>Колонки derived-таблицы/CTE (null — брать из кэша метаданных).</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>Смещение упоминания в тексте (для определения «текущего JOIN» перед ON).</summary>
    public int RefStart { get; init; }
}

/// <summary>CTE из WITH: имя + выведенные колонки (явный список или SELECT-список тела).</summary>
internal sealed record CteDef(string Name, IReadOnlyList<string> Columns);

/// <summary>
/// Элемент SELECT-списка: выходное имя (алиас после AS, неявный алиас или последняя часть
/// ссылки a.b), null для «*» и безымянных выражений.
/// </summary>
internal sealed record SelectItem(string? OutputName);

/// <summary>Метка клаузы (SELECT/FROM/WHERE/ON/ORDERBY/…) со смещением сразу после ключевого слова.</summary>
internal sealed record ClauseMark(string Name, int Offset);

/// <summary>Область видимости одного запроса/подзапроса/тела CTE.</summary>
internal sealed class QueryScope
{
    public QueryScope? Parent { get; set; }

    /// <summary>Диапазон области в исходном тексте (для поиска области каретки).</summary>
    public int Start { get; set; }

    public int End { get; set; }

    /// <summary>Таблицы FROM/JOIN/UPDATE (в порядке упоминания).</summary>
    public List<TableRef> Tables { get; } = [];

    /// <summary>CTE, объявленные WITH в этой области (видимы и вложенным областям).</summary>
    public Dictionary<string, CteDef> Ctes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<SelectItem> SelectItems { get; } = [];

    public List<ClauseMark> Clauses { get; } = [];

    public List<QueryScope> Children { get; } = [];

    /// <summary>Полуинтервал внутри скобок списка колонок INSERT INTO t (…).</summary>
    public (int Start, int End)? InsertColumnsRange { get; set; }

    /// <summary>Целевая таблица INSERT INTO.</summary>
    public TableRef? InsertTarget { get; set; }

    /// <summary>Цепочка областей: текущая → родительские (для резолва коррелированных алиасов).</summary>
    public IEnumerable<QueryScope> SelfAndParents()
    {
        for (var s = this; s is not null; s = s.Parent)
            yield return s;
    }

    /// <summary>Клауза, действующая в указанной позиции (последняя метка левее позиции).</summary>
    public string? ClauseAt(int offset)
    {
        string? best = null;
        var bestOff = -1;
        foreach (var c in Clauses)
        {
            if (c.Offset <= offset && c.Offset > bestOff)
            {
                best = c.Name;
                bestOff = c.Offset;
            }
        }
        return best;
    }

    /// <summary>Ищет CTE по имени в цепочке областей видимости.</summary>
    public CteDef? FindCte(string name)
    {
        foreach (var s in SelfAndParents())
        {
            if (s.Ctes.TryGetValue(name, out var cte))
                return cte;
        }
        return null;
    }
}

/// <summary>Результат семантического анализа: области видимости всех statement'ов текста.</summary>
internal sealed class SemanticModel
{
    public required List<QueryScope> Roots { get; init; }

    /// <summary>Самая глубокая область, содержащая позицию каретки.</summary>
    public QueryScope FindScopeAt(int caret)
    {
        var root = Roots[0];
        foreach (var r in Roots)
        {
            if (r.Start <= caret)
                root = r;
        }
        return Deepest(root, caret);
    }

    private static QueryScope Deepest(QueryScope scope, int caret)
    {
        foreach (var child in scope.Children)
        {
            if (child.Start <= caret && caret <= child.End)
                return Deepest(child, caret);
        }
        return scope;
    }
}
