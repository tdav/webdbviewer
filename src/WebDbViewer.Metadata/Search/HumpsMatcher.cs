namespace WebDbViewer.Metadata.Search;

/// <summary>
/// CamelCase/underscore-матчинг идентификаторов («humps», как в IDE):
/// «gp» находит get_price (первые буквы «горбов»),
/// «usrTbl» находит user_table (usr — подпоследовательность user, tbl — подпоследовательность table).
/// Правило: символы запроса распределяются по токенам идентификатора; в каждом задействованном
/// токене первый сопоставленный символ обязан совпасть с первым символом токена (якорь),
/// остальные — подпоследовательность токена. Регистр не учитывается.
/// </summary>
public static class HumpsMatcher
{
    public static bool IsMatch(string query, string identifier)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(identifier))
            return false;

        var tokens = Tokenize(identifier);
        if (tokens.Count == 0)
            return false;

        // Разделители в запросе ('_', '$' и т.п.) не значимы: "user_t" эквивалентен "usert"
        var q = string.Concat(query.Where(char.IsLetterOrDigit)).ToLowerInvariant();
        if (q.Length == 0)
            return false;

        return Match(q, 0, tokens, 0);
    }

    /// <summary>
    /// Разбивает идентификатор на токены-«горбы»: по '_'/'$'/'#'/цифро-буквенным границам
    /// и по переходам lower→Upper (camelCase). Токены — в нижнем регистре.
    /// </summary>
    public static List<string> Tokenize(string identifier)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush(identifier, tokens, ref start, i);
                continue;
            }
            if (start < 0)
            {
                start = i;
                continue;
            }
            var prev = identifier[i - 1];
            // Граница camelCase: строчная/цифра → заглавная; и буква → цифра
            var boundary = (char.IsUpper(c) && !char.IsUpper(prev) && char.IsLetterOrDigit(prev))
                           || (char.IsDigit(c) && char.IsLetter(prev));
            if (boundary)
            {
                Flush(identifier, tokens, ref start, i);
                start = i;
            }
        }
        Flush(identifier, tokens, ref start, identifier.Length);
        return tokens;

        static void Flush(string s, List<string> acc, ref int start, int end)
        {
            if (start >= 0 && end > start)
                acc.Add(s.Substring(start, end - start).ToLowerInvariant());
            start = -1;
        }
    }

    /// <summary>Рекурсивное сопоставление запроса токенам (с бэктрекингом; идентификаторы короткие).</summary>
    private static bool Match(string q, int qi, List<string> tokens, int ti)
    {
        if (qi == q.Length)
            return true;
        if (ti == tokens.Count)
            return false;

        // Вариант 1: пропустить текущий токен целиком
        if (Match(q, qi, tokens, ti + 1))
            return true;

        // Вариант 2: занять токен — якорь по первому символу токена
        var token = tokens[ti];
        if (token.Length == 0 || token[0] != q[qi])
            return false;

        // Потребляем символы запроса как подпоследовательность токена,
        // пробуя каждую точку отсечения (бэктрекинг)
        var qj = qi + 1;
        if (Match(q, qj, tokens, ti + 1))
            return true;

        var pos = 1; // позиция в токене после якорного символа
        while (qj < q.Length)
        {
            var found = token.IndexOf(q[qj], pos);
            if (found < 0)
                break;
            pos = found + 1;
            qj++;
            if (Match(q, qj, tokens, ti + 1))
                return true;
        }
        return false;
    }
}
