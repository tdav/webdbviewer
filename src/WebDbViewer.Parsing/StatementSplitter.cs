using WebDbViewer.Core;

namespace WebDbViewer.Parsing;

/// <summary>
/// Разбиение SQL-скрипта на statements быстрым лексерным проходом (без полного парса).
/// PostgreSQL: строки '...', E'...', dollar-quoting $$..$$ и $tag$..$tag$, комментарии
/// -- и /* */ (вложенные), квотированные идентификаторы "...".
/// Oracle: PL/SQL-блоки (BEGIN/DECLARE, CREATE [OR REPLACE] FUNCTION/PROCEDURE/PACKAGE/TRIGGER/TYPE)
/// до строки с одиночным «/», строки q'[...]', обычные statements по «;».
/// </summary>
public sealed class StatementSplitter : IStatementSplitter
{
    public IReadOnlyList<SqlStatement> Split(string script, DbKind dialect)
    {
        if (string.IsNullOrEmpty(script))
            return [];

        return dialect == DbKind.Oracle ? SplitOracle(script) : SplitPostgres(script);
    }

    // ================================================================== PostgreSQL

    private static IReadOnlyList<SqlStatement> SplitPostgres(string s)
    {
        var result = new List<SqlStatement>();
        var n = s.Length;
        var start = 0;
        var i = 0;

        while (i < n)
        {
            var c = s[i];

            if (c == '\'')
            {
                // Обычная или E-строка: E'..' определяем по предыдущему символу.
                var escaped = i > 0 && (s[i - 1] == 'E' || s[i - 1] == 'e')
                              && (i < 2 || !IsIdentChar(s[i - 2]));
                i = SkipSingleQuoted(s, i + 1, escaped);
                continue;
            }
            if (c == '"')
            {
                i = SkipDoubleQuoted(s, i + 1);
                continue;
            }
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i = SkipLineComment(s, i + 2);
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i = SkipBlockComment(s, i + 2, nested: true);
                continue;
            }
            if (c == '$')
            {
                // Dollar-quoting: $$ или $tag$. Не путать с параметрами $1.
                var tagEnd = TryReadDollarTag(s, i);
                if (tagEnd > 0)
                {
                    i = SkipDollarQuoted(s, i, tagEnd);
                    continue;
                }
                i++;
                continue;
            }
            if (c == ';')
            {
                AddTrimmed(s, start, i - start, result);
                start = i + 1;
                i++;
                continue;
            }
            i++;
        }

        AddTrimmed(s, start, n - start, result);
        return result;
    }

    /// <summary>
    /// Если в позиции pos начинается открывающий dollar-тег ($$ либо $tag$) —
    /// возвращает позицию сразу за ним, иначе -1.
    /// </summary>
    private static int TryReadDollarTag(string s, int pos)
    {
        var i = pos + 1;
        if (i < s.Length && s[i] == '$')
            return i + 1; // $$

        // $tag$: тег — буквы/цифры/подчёркивания, без «$» внутри.
        if (i >= s.Length || !(char.IsLetter(s[i]) || s[i] == '_'))
            return -1;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
            i++;
        return i < s.Length && s[i] == '$' ? i + 1 : -1;
    }

    /// <summary>Пропускает dollar-quoted строку; tagStart..tagEnd — открывающий тег вместе с «$».</summary>
    private static int SkipDollarQuoted(string s, int tagStart, int tagEnd)
    {
        var tag = s.AsSpan(tagStart, tagEnd - tagStart);
        var i = tagEnd;
        while (i < s.Length)
        {
            if (s[i] == '$' && i + tag.Length <= s.Length && s.AsSpan(i, tag.Length).SequenceEqual(tag))
                return i + tag.Length;
            i++;
        }
        return s.Length; // незакрытая строка — до конца скрипта
    }

    // ================================================================== Oracle

    private static IReadOnlyList<SqlStatement> SplitOracle(string s)
    {
        var result = new List<SqlStatement>();
        var n = s.Length;
        var start = 0;
        var i = 0;

        // Первые значимые слова текущего statement — для распознавания PL/SQL-блока.
        var words = new List<string>(8);
        var isBlock = false;

        void ResetStatement(int newStart)
        {
            start = newStart;
            words.Clear();
            isBlock = false;
        }

        while (i < n)
        {
            var c = s[i];

            if (c == '\'')
            {
                i = SkipSingleQuoted(s, i + 1, escaped: false);
                continue;
            }
            if ((c == 'q' || c == 'Q') && i + 1 < n && s[i + 1] == '\''
                && (i == 0 || !IsIdentChar(s[i - 1])))
            {
                i = SkipOracleQString(s, i);
                continue;
            }
            if (c == '"')
            {
                i = SkipDoubleQuoted(s, i + 1);
                continue;
            }
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                i = SkipLineComment(s, i + 2);
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                i = SkipBlockComment(s, i + 2, nested: false);
                continue;
            }
            if (c == '/' && IsLoneSlashOnLine(s, i))
            {
                // Строка с одиночным «/» — терминатор блока (и вообще текущего statement).
                AddTrimmed(s, start, i - start, result);
                i = SkipToLineEnd(s, i + 1);
                ResetStatement(i);
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var wStart = i;
                while (i < n && IsIdentChar(s[i])) i++;
                if (words.Count < 8)
                {
                    words.Add(s[wStart..i].ToUpperInvariant());
                    isBlock = isBlock || IsPlSqlBlockStart(words);
                }
                continue;
            }
            if (c == ';')
            {
                if (isBlock)
                {
                    // Внутри PL/SQL-блока «;» не разделяет — ждём строку с «/».
                    i++;
                    continue;
                }
                AddTrimmed(s, start, i - start, result);
                i++;
                ResetStatement(i);
                continue;
            }
            i++;
        }

        AddTrimmed(s, start, n - start, result);
        return result;
    }

    /// <summary>Начинается ли statement как PL/SQL-блок (по первым значимым словам).</summary>
    private static bool IsPlSqlBlockStart(List<string> words)
    {
        if (words.Count == 0)
            return false;
        var first = words[0];
        if (first is "BEGIN" or "DECLARE")
            return true;
        if (first != "CREATE")
            return false;

        // CREATE [OR REPLACE] [EDITIONABLE|NONEDITIONABLE] FUNCTION/PROCEDURE/PACKAGE/TRIGGER/TYPE
        foreach (var w in words.Skip(1))
        {
            if (w is "OR" or "REPLACE" or "EDITIONABLE" or "NONEDITIONABLE")
                continue;
            return w is "FUNCTION" or "PROCEDURE" or "PACKAGE" or "TRIGGER" or "TYPE" or "BODY";
        }
        return false;
    }

    /// <summary>q'X...X' — альтернативное квотирование Oracle; pos указывает на «q».</summary>
    private static int SkipOracleQString(string s, int pos)
    {
        var i = pos + 2; // за q'
        if (i >= s.Length)
            return s.Length;

        var open = s[i];
        var close = open switch
        {
            '[' => ']',
            '(' => ')',
            '{' => '}',
            '<' => '>',
            _ => open,
        };
        i++;
        while (i + 1 < s.Length)
        {
            if (s[i] == close && s[i + 1] == '\'')
                return i + 2;
            i++;
        }
        return s.Length;
    }

    /// <summary>Стоит ли «/» в позиции pos на строке один (только пробелы вокруг).</summary>
    private static bool IsLoneSlashOnLine(string s, int pos)
    {
        var i = pos - 1;
        while (i >= 0 && s[i] != '\n')
        {
            if (!char.IsWhiteSpace(s[i]))
                return false;
            i--;
        }
        i = pos + 1;
        while (i < s.Length && s[i] != '\n')
        {
            if (!char.IsWhiteSpace(s[i]))
                return false;
            i++;
        }
        return true;
    }

    private static int SkipToLineEnd(string s, int pos)
    {
        while (pos < s.Length && s[pos] != '\n') pos++;
        return pos < s.Length ? pos + 1 : pos;
    }

    // ================================================================== Общие примитивы

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

    /// <summary>Пропускает '...'-строку; '' — экранированная кавычка; escaped — учитывать \'.</summary>
    private static int SkipSingleQuoted(string s, int pos, bool escaped)
    {
        var i = pos;
        while (i < s.Length)
        {
            if (escaped && s[i] == '\\' && i + 1 < s.Length)
            {
                i += 2;
                continue;
            }
            if (s[i] == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static int SkipDoubleQuoted(string s, int pos)
    {
        var i = pos;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                if (i + 1 < s.Length && s[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static int SkipLineComment(string s, int pos)
    {
        while (pos < s.Length && s[pos] != '\n') pos++;
        return pos;
    }

    /// <summary>Блочный комментарий; nested — поддержка вложенности (PostgreSQL).</summary>
    private static int SkipBlockComment(string s, int pos, bool nested)
    {
        var depth = 1;
        var i = pos;
        while (i + 1 < s.Length)
        {
            if (s[i] == '*' && s[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                    return i;
                continue;
            }
            if (nested && s[i] == '/' && s[i + 1] == '*')
            {
                depth++;
                i += 2;
                continue;
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>Добавляет statement, отбрасывая пустые; Offset — первый непробельный символ.</summary>
    private static void AddTrimmed(string script, int offset, int length, List<SqlStatement> result)
    {
        var s = offset;
        var e = offset + length;
        while (s < e && char.IsWhiteSpace(script[s])) s++;
        while (e > s && char.IsWhiteSpace(script[e - 1])) e--;
        if (e <= s)
            return;
        result.Add(new SqlStatement
        {
            Text = script[s..e],
            Offset = s,
            Length = e - s,
        });
    }
}
