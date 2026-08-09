namespace WebDbViewer.Completion.Semantics;

/// <summary>Текстовые утилиты позиции каретки: начатое слово, квалификатор, предыдущее слово.</summary>
internal static class CaretText
{
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#';

    /// <summary>Начатое слово непосредственно перед кареткой (для фильтрации кандидатов).</summary>
    public static string WordPrefix(string beforeCaret)
    {
        var i = beforeCaret.Length;
        while (i > 0 && IsWordChar(beforeCaret[i - 1]))
            i--;
        return beforeCaret[i..];
    }

    /// <summary>Предыдущее значимое слово перед кареткой (текст уже без начатого слова), UPPER.</summary>
    public static string? PreviousWord(string beforeWord)
    {
        var i = beforeWord.Length;
        while (i > 0 && char.IsWhiteSpace(beforeWord[i - 1]))
            i--;
        var end = i;
        while (i > 0 && IsWordChar(beforeWord[i - 1]))
            i--;
        return end > i ? beforeWord[i..end].ToUpperInvariant() : null;
    }

    /// <summary>Последний значимый символ перед кареткой — запятая (позиция таблицы в списке FROM).</summary>
    public static bool EndsWithComma(string beforeWord)
    {
        var i = beforeWord.Length;
        while (i > 0 && char.IsWhiteSpace(beforeWord[i - 1]))
            i--;
        return i > 0 && beforeWord[i - 1] == ',';
    }

    /// <summary>Каретка стоит сразу после оператора приведения типа PostgreSQL «::».</summary>
    public static bool EndsWithCastOperator(string beforeWord)
    {
        var i = beforeWord.Length;
        while (i > 0 && char.IsWhiteSpace(beforeWord[i - 1]))
            i--;
        return i >= 2 && beforeWord[i - 1] == ':' && beforeWord[i - 2] == ':';
    }

    /// <summary>
    /// Слова, после которых скобка открывает не вызов функции: «SELECT (a + b)», «id IN (…)»,
    /// «EXISTS (…)», «OVER (…)». Без этого списка именем функции считается любое слово слева.
    /// </summary>
    private static readonly HashSet<string> NotCallableBeforeParen = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "VALUES", "IN", "EXISTS", "AND", "OR", "NOT", "ON", "BY",
        "ALL", "ANY", "SOME", "INTO", "SET", "UNION", "INTERSECT", "EXCEPT", "CASE", "WHEN",
        "THEN", "ELSE", "RETURNING", "USING", "JOIN", "AS", "OVER", "GROUP", "ORDER", "HAVING",
        "LIMIT", "OFFSET", "DISTINCT", "WITH", "TABLE", "UPDATE", "DELETE", "INSERT", "IS",
        "BETWEEN", "LIKE", "PARTITION", "WITHIN", "FILTER", "LATERAL", "PRIMARY", "FOREIGN",
        "UNIQUE", "CHECK", "REFERENCES", "CONSTRAINT", "KEY", "COLUMNS",
    };

    /// <summary>
    /// Вызов, внутри скобок которого стоит каретка: имя функции и номер аргумента с нуля.
    /// «round(x, |» → («round», 1). null — каретка не внутри вызова.
    /// ponytail: скобки и запятые внутри строковых литералов считаются наравне с обычными;
    /// для подсказки аргументов этого достаточно, полный лексер здесь не окупается.
    /// </summary>
    public static (string Name, int ArgumentIndex)? EnclosingCall(string beforeWord)
    {
        var depth = 0;
        var argumentIndex = 0;
        for (var i = beforeWord.Length - 1; i >= 0; i--)
        {
            var c = beforeWord[i];
            if (c == ')')
            {
                depth++;
            }
            else if (c == '(')
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }
                var end = i;
                while (end > 0 && char.IsWhiteSpace(beforeWord[end - 1]))
                    end--;
                var start = end;
                while (start > 0 && IsWordChar(beforeWord[start - 1]))
                    start--;
                if (start == end)
                    return null;
                var name = beforeWord[start..end];
                return NotCallableBeforeParen.Contains(name) ? null : (name, argumentIndex);
            }
            else if (c == ',' && depth == 0)
            {
                argumentIndex++;
            }
        }
        return null;
    }

    /// <summary>
    /// Цепочка квалификаторов перед кареткой: «a.| » → [a]; «schema.table.| » → [schema, table].
    /// Кавычки идентификаторов снимаются. Пустой список — квалификатора нет.
    /// </summary>
    public static List<string> QualifierChain(string beforeWord)
    {
        var parts = new List<string>();
        var i = beforeWord.Length;
        while (true)
        {
            while (i > 0 && char.IsWhiteSpace(beforeWord[i - 1]))
                i--;
            if (i == 0 || beforeWord[i - 1] != '.')
                break;
            i--; // «.»
            while (i > 0 && char.IsWhiteSpace(beforeWord[i - 1]))
                i--;
            if (i > 0 && beforeWord[i - 1] == '"')
            {
                var close = i - 1;
                var open = close > 0 ? beforeWord.LastIndexOf('"', close - 1) : -1;
                if (open < 0)
                {
                    parts.Clear();
                    break;
                }
                parts.Insert(0, beforeWord[(open + 1)..close]);
                i = open;
            }
            else
            {
                var end = i;
                while (i > 0 && IsWordChar(beforeWord[i - 1]))
                    i--;
                if (end == i)
                {
                    // «).» и т.п. — выражение, а не идентификатор: не считаем квалификатором.
                    parts.Clear();
                    break;
                }
                parts.Insert(0, beforeWord[i..end]);
            }
        }
        return parts;
    }
}
