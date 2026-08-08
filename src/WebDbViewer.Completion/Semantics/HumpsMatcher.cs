using System.Text;

namespace WebDbViewer.Completion.Semantics;

/// <summary>
/// Матчинг введённого пользователем префикса против кандидата: обычный префикс либо
/// «humps»-совпадение по сегментам snake_case/camelCase («ui» → user_id, «oi» → orderItems).
/// </summary>
internal static class HumpsMatcher
{
    /// <summary>Штраф к SortPriority для humps-совпадения (префиксное ранжируется выше).</summary>
    public const int HumpsPenalty = 2;

    /// <summary>null — кандидат не подходит; 0 — префиксное совпадение; HumpsPenalty — humps-совпадение.</summary>
    public static int? MatchRank(string query, string candidate)
    {
        if (string.IsNullOrEmpty(query))
            return 0;
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 0;
        return Matches(query, SplitSegments(candidate), 0, 0) ? HumpsPenalty : null;
    }

    /// <summary>Рекурсивно: каждый сегмент либо пропускается, либо покрывает непустой префикс запроса.</summary>
    private static bool Matches(string q, List<string> segs, int qi, int si)
    {
        if (qi >= q.Length)
            return true;
        if (si >= segs.Count)
            return false;
        // Вариант 1: пропустить сегмент целиком.
        if (Matches(q, segs, qi, si + 1))
            return true;
        // Вариант 2: сегмент покрывает непустой префикс остатка запроса.
        var seg = segs[si];
        var k = 0;
        while (k < seg.Length && qi + k < q.Length
               && char.ToLowerInvariant(seg[k]) == char.ToLowerInvariant(q[qi + k]))
        {
            k++;
            if (Matches(q, segs, qi + k, si + 1))
                return true;
        }
        return false;
    }

    /// <summary>Сегменты идентификатора: границы «_», «$», «#», «.» и переходы lower→Upper.</summary>
    private static List<string> SplitSegments(string s)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var c in s)
        {
            if (c is '_' or '$' or '#' or '.')
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            if (char.IsUpper(c) && current.Length > 0 && !char.IsUpper(current[^1]))
            {
                result.Add(current.ToString());
                current.Clear();
            }
            current.Append(c);
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }
}
