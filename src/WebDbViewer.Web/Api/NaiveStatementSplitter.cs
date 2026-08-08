using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>
/// Наивный фолбэк-сплиттер скрипта по «;» с учётом строковых литералов,
/// квотированных идентификаторов и комментариев.
/// TODO: заменить на полноценную реализацию IStatementSplitter из WebDbViewer.Parsing
/// (dollar-quoting PostgreSQL, PL/SQL-блоки, слэш-терминатор) — используется автоматически,
/// как только она появится в DI.
/// </summary>
public sealed class NaiveStatementSplitter : IStatementSplitter
{
    public static readonly NaiveStatementSplitter Instance = new();

    public IReadOnlyList<SqlStatement> Split(string script, DbKind dialect)
    {
        var result = new List<SqlStatement>();
        if (string.IsNullOrEmpty(script))
            return result;

        var n = script.Length;
        var start = 0;
        var i = 0;
        while (i < n)
        {
            var c = script[i];
            if (c == '\'')
            {
                // Строковый литерал: '' — экранированная кавычка.
                i++;
                while (i < n)
                {
                    if (script[i] == '\'')
                    {
                        if (i + 1 < n && script[i + 1] == '\'') { i += 2; continue; }
                        break;
                    }
                    i++;
                }
            }
            else if (c == '"')
            {
                // Квотированный идентификатор.
                i++;
                while (i < n && script[i] != '"') i++;
            }
            else if (c == '-' && i + 1 < n && script[i + 1] == '-')
            {
                // Однострочный комментарий.
                while (i < n && script[i] != '\n') i++;
                continue;
            }
            else if (c == '/' && i + 1 < n && script[i + 1] == '*')
            {
                // Блочный комментарий.
                i += 2;
                while (i + 1 < n && !(script[i] == '*' && script[i + 1] == '/')) i++;
                i++;
            }
            else if (c == ';')
            {
                AddTrimmed(script, start, i - start, result);
                start = i + 1;
            }
            i++;
        }
        AddTrimmed(script, start, n - start, result);
        return result;
    }

    /// <summary>Добавляет statement, отбрасывая пустые и вычисляя смещение без обрамляющих пробелов.</summary>
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
