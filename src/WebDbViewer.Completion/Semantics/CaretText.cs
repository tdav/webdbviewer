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
