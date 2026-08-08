namespace WebDbViewer.Completion.Semantics;

/// <summary>
/// Построение таблицы областей видимости по потоку лексем: FROM/JOIN (alias → таблица),
/// подзапросы (вложенные области), CTE (WITH x AS (…)), LATERAL, INSERT INTO (…), UPDATE … SET.
/// Устойчив к неполному SQL (текст в момент набора): рекурсивный спуск без жёсткой грамматики.
/// </summary>
internal static class ScopeAnalyzer
{
    private const int MaxNesting = 64;

    /// <summary>Слова, которые не могут быть алиасом таблицы или началом ссылки на таблицу во FROM.</summary>
    private static readonly HashSet<string> ReservedInFrom = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE", "ON", "JOIN", "INNER", "LEFT", "RIGHT", "FULL", "CROSS", "NATURAL", "OUTER", "LATERAL",
        "GROUP", "ORDER", "BY", "SET", "VALUES", "USING", "LIMIT", "OFFSET", "HAVING", "AS",
        "UNION", "INTERSECT", "EXCEPT", "RETURNING", "FETCH", "FOR", "WINDOW", "SELECT", "START", "CONNECT",
        "WITH", "INSERT", "UPDATE", "DELETE", "INTO", "FROM", "AND", "OR", "NOT",
        "TABLESAMPLE", "ONLY", "WHEN", "THEN", "ELSE", "END", "CASE", "DISTINCT", "ALL",
    };

    public static SemanticModel Analyze(string sql)
    {
        var lex = SqlMiniLexer.Tokenize(sql);
        var roots = new List<QueryScope>();
        var i = 0;
        while (i < lex.Count)
        {
            var start = lex[i].Start;
            roots.Add(ParseQuery(sql, lex, ref i, null, start, isSub: false, nesting: 0));
        }
        if (roots.Count == 0)
            roots.Add(new QueryScope { Start = 0, End = sql.Length });
        return new SemanticModel { Roots = roots };
    }

    /// <summary>Разбирает один запрос/подзапрос. Для подзапроса потребляет закрывающую скобку.</summary>
    private static QueryScope ParseQuery(
        string sql, List<Lexeme> lex, ref int i, QueryScope? parent, int start, bool isSub, int nesting)
    {
        var scope = new QueryScope { Parent = parent, Start = start, End = sql.Length };
        var depth = 0;          // скобки выражений (не подзапросов) на уровне этой области
        var clause = "";
        var pendingBy = "";     // «GROUP» / «ORDER» в ожидании «BY»
        var selectRanges = new List<(int S, int E)>(); // диапазоны SELECT-списков в индексах лексем
        var selStart = -1;

        void CloseSelect(int endIdx)
        {
            if (selStart >= 0)
            {
                selectRanges.Add((selStart, endIdx));
                selStart = -1;
            }
        }

        QueryScope Done(int endOffset, int endLexIdx)
        {
            CloseSelect(endLexIdx);
            scope.End = endOffset;
            FinalizeSelectItems(scope, lex, selectRanges);
            return scope;
        }

        while (i < lex.Count)
        {
            var t = lex[i];

            if (t.Kind == LexKind.Punct)
            {
                switch (t.Text)
                {
                    case "(":
                        // Список колонок INSERT INTO t (…): запоминаем диапазон для контекста каретки.
                        if (depth == 0 && clause == "INSERT" && scope.InsertTarget is not null
                            && scope.InsertColumnsRange is null)
                        {
                            var open = t.End;
                            i++;
                            var d2 = 1;
                            var close = sql.Length;
                            while (i < lex.Count)
                            {
                                if (lex[i].IsPunct("(")) d2++;
                                else if (lex[i].IsPunct(")"))
                                {
                                    d2--;
                                    if (d2 == 0) { close = lex[i].Start; i++; break; }
                                }
                                i++;
                            }
                            scope.InsertColumnsRange = (open, close);
                            continue;
                        }
                        // Подзапрос: «( SELECT/WITH …».
                        if (nesting < MaxNesting && IsSubqueryStart(lex, i + 1))
                        {
                            var subStart = t.End;
                            var openStart = t.Start;
                            i++;
                            var child = ParseQuery(sql, lex, ref i, scope, subStart, isSub: true, nesting + 1);
                            scope.Children.Add(child);
                            // Derived-таблица во FROM: «(SELECT …) [AS] alias [(col, …)]».
                            if (depth == 0 && clause == "FROM")
                            {
                                var (alias, cols) = ReadDerivedAlias(lex, ref i);
                                scope.Tables.Add(new TableRef
                                {
                                    Name = "(подзапрос)",
                                    Alias = alias,
                                    Columns = cols ?? OutputColumns(child),
                                    RefStart = openStart,
                                });
                            }
                            continue;
                        }
                        depth++;
                        i++;
                        continue;

                    case ")":
                        if (depth > 0) { depth--; i++; continue; }
                        if (isSub)
                        {
                            var idx = i;
                            i++; // потребляем «)» подзапроса
                            return Done(t.Start, idx);
                        }
                        i++; // лишняя скобка в корне — игнор
                        continue;

                    case ";":
                        if (depth == 0)
                        {
                            var idx = i;
                            if (!isSub) i++; // конец statement'а; для подзапроса — пусть закроют родители
                            return Done(t.Start, idx);
                        }
                        i++;
                        continue;

                    default:
                        i++;
                        continue;
                }
            }

            if (t.IsName)
            {
                var up = t.Kind == LexKind.Word ? t.Text.ToUpperInvariant() : "";
                if (depth == 0)
                {
                    switch (up)
                    {
                        case "WITH" when clause == "":
                            i++;
                            ParseCteList(sql, lex, ref i, scope, nesting);
                            continue;
                        case "SELECT":
                            CloseSelect(i);
                            clause = "SELECT";
                            scope.Clauses.Add(new ClauseMark("SELECT", t.End));
                            i++;
                            // DISTINCT/ALL не входят в SELECT-список.
                            while (i < lex.Count && (lex[i].IsWord("DISTINCT") || lex[i].IsWord("ALL")))
                                i++;
                            selStart = i;
                            continue;
                        case "FROM":
                            CloseSelect(i);
                            clause = "FROM";
                            scope.Clauses.Add(new ClauseMark("FROM", t.End));
                            i++;
                            continue;
                        case "JOIN":
                            clause = "FROM";
                            scope.Clauses.Add(new ClauseMark("FROM", t.End));
                            i++;
                            continue;
                        case "INNER" or "LEFT" or "RIGHT" or "FULL" or "CROSS" or "NATURAL" or "OUTER" or "LATERAL" or "ONLY":
                            i++;
                            continue;
                        case "ON" when clause is "FROM" or "ON":
                            clause = "ON";
                            scope.Clauses.Add(new ClauseMark("ON", t.End));
                            i++;
                            continue;
                        case "WHERE":
                            CloseSelect(i);
                            clause = "WHERE";
                            scope.Clauses.Add(new ClauseMark("WHERE", t.End));
                            i++;
                            continue;
                        case "GROUP" or "ORDER":
                            pendingBy = up;
                            i++;
                            continue;
                        case "BY" when pendingBy != "":
                            clause = pendingBy == "GROUP" ? "GROUPBY" : "ORDERBY";
                            scope.Clauses.Add(new ClauseMark(clause, t.End));
                            pendingBy = "";
                            i++;
                            continue;
                        case "HAVING":
                            clause = "HAVING";
                            scope.Clauses.Add(new ClauseMark("HAVING", t.End));
                            i++;
                            continue;
                        case "INSERT":
                            clause = "PREINSERT";
                            i++;
                            continue;
                        case "INTO" when clause is "PREINSERT" or "":
                            clause = "INSERT";
                            scope.Clauses.Add(new ClauseMark("INSERT", t.End));
                            i++;
                            scope.InsertTarget = TryParseTableRef(lex, ref i, scope);
                            if (scope.InsertTarget is not null)
                                scope.Tables.Add(scope.InsertTarget);
                            continue;
                        case "UPDATE" when clause == "":
                            clause = "UPDATE";
                            scope.Clauses.Add(new ClauseMark("UPDATE", t.End));
                            i++;
                            var target = TryParseTableRef(lex, ref i, scope);
                            if (target is not null)
                                scope.Tables.Add(target);
                            continue;
                        case "DELETE":
                            i++; // дальше будет FROM
                            continue;
                        case "SET" when clause == "UPDATE":
                            clause = "SET";
                            scope.Clauses.Add(new ClauseMark("SET", t.End));
                            i++;
                            continue;
                        case "VALUES":
                            clause = "VALUES";
                            scope.Clauses.Add(new ClauseMark("VALUES", t.End));
                            i++;
                            continue;
                        case "UNION" or "INTERSECT" or "EXCEPT":
                            clause = ""; // следующий SELECT — в этой же области
                            i++;
                            continue;
                    }

                    // Ссылка на таблицу во FROM/JOIN.
                    if (clause == "FROM" && !(t.Kind == LexKind.Word && ReservedInFrom.Contains(t.Text)))
                    {
                        var tr = TryParseTableRef(lex, ref i, scope);
                        if (tr is not null)
                        {
                            scope.Tables.Add(tr);
                            continue;
                        }
                    }
                }
                i++;
                continue;
            }

            i++; // Number / Str
        }

        return Done(sql.Length, lex.Count);
    }

    private static bool IsSubqueryStart(List<Lexeme> lex, int i) =>
        i < lex.Count && (lex[i].IsWord("SELECT") || lex[i].IsWord("WITH"));

    /// <summary>WITH [RECURSIVE] name [(col,…)] AS [[NOT] MATERIALIZED] ( … ) [, …]</summary>
    private static void ParseCteList(string sql, List<Lexeme> lex, ref int i, QueryScope scope, int nesting)
    {
        if (i < lex.Count && lex[i].IsWord("RECURSIVE"))
            i++;
        while (i < lex.Count)
        {
            if (!lex[i].IsName)
                break;
            var name = lex[i].Text;
            i++;

            List<string>? explicitCols = null;
            if (i < lex.Count && lex[i].IsPunct("("))
            {
                explicitCols = [];
                i++;
                while (i < lex.Count && !lex[i].IsPunct(")"))
                {
                    if (lex[i].IsName)
                        explicitCols.Add(lex[i].Text);
                    i++;
                }
                if (i < lex.Count)
                    i++; // «)»
            }

            if (i < lex.Count && lex[i].IsWord("AS"))
                i++;
            while (i < lex.Count && (lex[i].IsWord("NOT") || lex[i].IsWord("MATERIALIZED")))
                i++;

            if (i < lex.Count && lex[i].IsPunct("(") && nesting < MaxNesting)
            {
                var subStart = lex[i].End;
                i++;
                var child = ParseQuery(sql, lex, ref i, scope, subStart, isSub: true, nesting + 1);
                scope.Children.Add(child);
                scope.Ctes[name] = new CteDef(name, explicitCols ?? OutputColumns(child));
            }
            else
            {
                // Незавершённое объявление (набор текста) — регистрируем имя без колонок.
                scope.Ctes[name] = new CteDef(name, explicitCols ?? []);
                break;
            }

            if (i < lex.Count && lex[i].IsPunct(","))
            {
                i++;
                continue;
            }
            break;
        }
    }

    /// <summary>[schema.]table [AS] [alias]; для имени, совпадающего с CTE, — виртуальная таблица.</summary>
    private static TableRef? TryParseTableRef(List<Lexeme> lex, ref int i, QueryScope scope)
    {
        if (i >= lex.Count || !lex[i].IsName)
            return null;
        var refStart = lex[i].Start;
        var parts = new List<string> { lex[i].Text };
        i++;
        while (i + 1 < lex.Count && lex[i].IsPunct(".") && lex[i + 1].IsName)
        {
            parts.Add(lex[i + 1].Text);
            i += 2;
        }

        var schema = parts.Count >= 2 ? parts[^2] : null;
        var name = parts[^1];

        string? alias = null;
        if (i < lex.Count)
        {
            var j = i;
            if (lex[j].IsWord("AS") && j + 1 < lex.Count)
                j++;
            if (j < lex.Count && lex[j].IsName
                && !(lex[j].Kind == LexKind.Word && ReservedInFrom.Contains(lex[j].Text)))
            {
                alias = lex[j].Text;
                i = j + 1;
            }
        }

        var cte = schema is null ? scope.FindCte(name) : null;
        return new TableRef
        {
            Schema = schema,
            Name = name,
            Alias = alias,
            IsCte = cte is not null,
            Columns = cte?.Columns,
            RefStart = refStart,
        };
    }

    /// <summary>Алиас derived-таблицы после «)»: [AS] alias [(col, …)].</summary>
    private static (string? Alias, List<string>? Cols) ReadDerivedAlias(List<Lexeme> lex, ref int i)
    {
        string? alias = null;
        List<string>? cols = null;
        var j = i;
        if (j < lex.Count && lex[j].IsWord("AS"))
            j++;
        if (j < lex.Count && lex[j].IsName
            && !(lex[j].Kind == LexKind.Word && ReservedInFrom.Contains(lex[j].Text)))
        {
            alias = lex[j].Text;
            i = j + 1;
            if (i < lex.Count && lex[i].IsPunct("("))
            {
                // (col1, col2) — алиасы колонок derived-таблицы.
                cols = [];
                var k = i + 1;
                var ok = true;
                while (k < lex.Count && !lex[k].IsPunct(")"))
                {
                    if (lex[k].IsName) cols.Add(lex[k].Text);
                    else if (!lex[k].IsPunct(",")) { ok = false; break; }
                    k++;
                }
                if (ok && k < lex.Count)
                    i = k + 1;
                else
                    cols = null;
            }
        }
        return (alias, cols);
    }

    /// <summary>Выходные колонки области (для CTE/derived): непустые имена SELECT-списка.</summary>
    private static IReadOnlyList<string> OutputColumns(QueryScope scope) =>
        scope.SelectItems.Where(x => x.OutputName is not null).Select(x => x.OutputName!).ToList();

    /// <summary>Разбор SELECT-списков: top-level запятые → элементы → выходные имена.</summary>
    private static void FinalizeSelectItems(QueryScope scope, List<Lexeme> lex, List<(int S, int E)> ranges)
    {
        foreach (var (s, e) in ranges)
        {
            var d = 0;
            var itemStart = s;
            for (var k = s; k <= e; k++)
            {
                var atEnd = k == e;
                if (!atEnd && lex[k].Kind == LexKind.Punct)
                {
                    if (lex[k].Text == "(") { d++; continue; }
                    if (lex[k].Text == ")") { d--; continue; }
                }
                if (atEnd || (d == 0 && lex[k].IsPunct(",")))
                {
                    AddSelectItem(scope, lex, itemStart, k);
                    itemStart = k + 1;
                }
            }
        }
    }

    /// <summary>Выходное имя элемента SELECT-списка [s, e): AS-алиас, неявный алиас или последняя часть ссылки.</summary>
    private static void AddSelectItem(QueryScope scope, List<Lexeme> lex, int s, int e)
    {
        if (e <= s)
            return;
        var d = 0;
        for (var k = s; k < e; k++)
        {
            if (lex[k].Kind == LexKind.Punct)
            {
                if (lex[k].Text == "(") d++;
                else if (lex[k].Text == ")") d--;
                continue;
            }
            if (d == 0 && lex[k].IsWord("AS") && k + 1 < e && lex[k + 1].IsName)
            {
                scope.SelectItems.Add(new SelectItem(lex[k + 1].Text));
                return;
            }
        }
        var last = lex[e - 1];
        if (last.IsName && !(last.Kind == LexKind.Word && ReservedInFrom.Contains(last.Text)))
        {
            scope.SelectItems.Add(new SelectItem(last.Text));
            return;
        }
        scope.SelectItems.Add(new SelectItem(null)); // «*», выражение без алиаса и т.п.
    }
}
