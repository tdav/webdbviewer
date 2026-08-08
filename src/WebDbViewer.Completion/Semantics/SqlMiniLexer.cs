using System.Text;

namespace WebDbViewer.Completion.Semantics;

/// <summary>Вид лексемы упрощённого SQL-лексера.</summary>
internal enum LexKind { Word, Quoted, Number, Str, Punct }

/// <summary>Лексема: [Start, End) — полуинтервал в исходном тексте. Text для Quoted — без кавычек.</summary>
internal readonly record struct Lexeme(LexKind Kind, string Text, int Start, int End)
{
    public bool IsWord(string upper) =>
        Kind == LexKind.Word && string.Equals(Text, upper, StringComparison.OrdinalIgnoreCase);

    public bool IsPunct(string p) => Kind == LexKind.Punct && Text == p;

    public bool IsName => Kind is LexKind.Word or LexKind.Quoted;
}

/// <summary>
/// Упрощённый SQL-лексер для семантического анализа областей видимости: слова,
/// квотированные идентификаторы, строки (включая '' и dollar-quoting), числа, пунктуация.
/// Комментарии пропускаются. Устойчив к незавершённому тексту (обрыв строки/комментария не фатален).
/// </summary>
internal static class SqlMiniLexer
{
    public static List<Lexeme> Tokenize(string sql)
    {
        var result = new List<Lexeme>();
        var i = 0;
        var n = sql.Length;
        while (i < n)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // «-- …» — комментарий до конца строки.
            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n') i++;
                continue;
            }

            // «/* … */» — блочный комментарий (вложенный, как в PostgreSQL).
            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                var depth = 1;
                i += 2;
                while (i < n && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') { depth++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') { depth--; i += 2; }
                    else i++;
                }
                continue;
            }

            // Строковый литерал '…' с экранированием ''.
            if (c == '\'')
            {
                var start = i;
                i++;
                while (i < n)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < n && sql[i + 1] == '\'') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                result.Add(new Lexeme(LexKind.Str, sql[start..i], start, i));
                continue;
            }

            // Квотированный идентификатор "…" с экранированием "".
            if (c == '"')
            {
                var start = i;
                i++;
                var sb = new StringBuilder();
                while (i < n)
                {
                    if (sql[i] == '"')
                    {
                        if (i + 1 < n && sql[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(sql[i]);
                    i++;
                }
                result.Add(new Lexeme(LexKind.Quoted, sb.ToString(), start, i));
                continue;
            }

            // Dollar-quoting: $tag$ … $tag$.
            if (c == '$')
            {
                var m = i + 1;
                while (m < n && (char.IsLetterOrDigit(sql[m]) || sql[m] == '_')) m++;
                if (m < n && sql[m] == '$')
                {
                    var tag = sql[i..(m + 1)];
                    var close = sql.IndexOf(tag, m + 1, StringComparison.Ordinal);
                    var end = close < 0 ? n : close + tag.Length;
                    result.Add(new Lexeme(LexKind.Str, sql[i..end], i, end));
                    i = end;
                    continue;
                }
            }

            // Слово (идентификатор/ключевое слово).
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#')) i++;
                result.Add(new Lexeme(LexKind.Word, sql[start..i], start, i));
                continue;
            }

            // Число (включая 1.5 — точка внутри числа не является квалификатором).
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.')) i++;
                result.Add(new Lexeme(LexKind.Number, sql[start..i], start, i));
                continue;
            }

            result.Add(new Lexeme(LexKind.Punct, sql[i].ToString(), i, i + 1));
            i++;
        }
        return result;
    }
}
