using WebDbViewer.Completion.Semantics;
using WebDbViewer.Core;

namespace WebDbViewer.Completion;

/// <summary>
/// Семантическое автодополнение v1: области видимости (FROM/JOIN, CTE, подзапросы, LATERAL),
/// резолв «квалификатор.», FK-сниппеты для JOIN … ON, INSERT INTO (…), ORDER/GROUP BY,
/// автоалиасы, раскрытие t.*, humps-матчинг и ранжирование по близости к текущему scope.
/// Возвращает null, если контекст не распознан — вызывающий откатывается на v0-эвристику.
/// </summary>
internal sealed class SemanticCompleter
{
    private static class Priority
    {
        public const int FkJoinSnippet = 0;   // готовое условие JOIN … ON по FK — всегда сверху
        public const int StarExpand = 1;      // «t.* → колонки»
        public const int SelectAlias = 4;     // алиасы SELECT-списка в ORDER/GROUP BY
        public const int ScopeColumn = 5;     // колонки таблиц текущего scope
        public const int FkRelatedTable = 6;  // таблицы с FK-связями к уже используемым
        public const int ContextTable = 8;    // прочие таблицы в позиции таблицы
        public const int OtherColumn = 10;    // колонки вне распознанного scope
        public const int Routine = 12;        // функции и процедуры схемы
        public const int SchemaName = 15;
    }

    /// <summary>Слова, после которых каретка стоит в позиции имени таблицы.</summary>
    private static readonly HashSet<string> TableContextWords =
        new(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN", "INTO", "UPDATE", "TABLE", "LATERAL" };

    private readonly IMetadataCache _metadata;

    public SemanticCompleter(IMetadataCache metadata) => _metadata = metadata;

    public async Task<List<CompletionItem>?> TryCompleteAsync(
        CompletionRequest request, DbKind dialect, CompletionOptions options,
        SchemaSnapshot? defaultSnapshot, bool grammarSuggestsColumns, CancellationToken ct)
    {
        var sql = request.SqlText ?? string.Empty;
        var caret = Math.Clamp(request.CaretOffset, 0, sql.Length);
        var beforeCaret = sql[..caret];
        var wordPrefix = CaretText.WordPrefix(beforeCaret);
        var beforeWord = beforeCaret[..^wordPrefix.Length];
        var qualifier = CaretText.QualifierChain(beforeWord);
        var prevWord = CaretText.PreviousWord(beforeWord);

        var model = ScopeAnalyzer.Analyze(sql);
        var scope = model.FindScopeAt(caret);
        var clause = scope.ClauseAt(caret);
        var snapshots = await GetSnapshotsAsync(request, defaultSnapshot, ct);

        var items = new List<CompletionItem>(64);

        // ---------- «квалификатор.»: колонки алиаса/таблицы/CTE или объекты схемы.
        if (qualifier.Count > 0)
        {
            var handled = await CompleteQualifierAsync(
                items, qualifier, scope, snapshots, wordPrefix, dialect, request, ct);
            return handled ? items : null;
        }

        // ---------- INSERT INTO t (…): колонки цели без generated.
        if (scope.InsertColumnsRange is { } range && caret >= range.Start && caret <= range.End
            && scope.InsertTarget is not null)
        {
            var src = FindColumns(scope.InsertTarget, snapshots);
            if (src is null)
                return null;
            foreach (var c in src.Value.Columns)
            {
                if (!c.IsGenerated)
                    AddColumnItem(items, c, src.Value.Display, wordPrefix, Priority.ScopeColumn, dialect);
            }
            return items;
        }

        // ---------- Позиция имени таблицы: FROM/JOIN/INTO/UPDATE/«,» в списке FROM.
        var tablePosition = (prevWord is not null && TableContextWords.Contains(prevWord))
                            || (clause == "FROM" && CaretText.EndsWithComma(beforeWord));
        if (tablePosition)
        {
            var autoAlias = options.AutoAliasTables && prevWord is not ("INTO" or "UPDATE" or "TABLE");
            await AddTablesAsync(items, request, scope, snapshots, wordPrefix, autoAlias, dialect, ct);
            return items;
        }

        // ---------- JOIN … ON: готовые условия по FK + колонки scope.
        if (clause == "ON" && prevWord == "ON")
        {
            AddFkJoinSnippets(items, scope, snapshots, caret, wordPrefix, dialect);
            AddScopeColumns(items, scope, snapshots, wordPrefix, dialect, defaultSnapshot);
            AddRoutines(items, snapshots, wordPrefix, dialect);
            return items;
        }

        // ---------- ORDER BY / GROUP BY: алиасы SELECT-списка + колонки.
        if (clause is "ORDERBY" or "GROUPBY")
        {
            AddSelectAliases(items, scope, wordPrefix, dialect);
            AddScopeColumns(items, scope, snapshots, wordPrefix, dialect, defaultSnapshot);
            AddRoutines(items, snapshots, wordPrefix, dialect);
            return items;
        }

        // ---------- Прочие «колоночные» контексты.
        if (clause is "SELECT" or "WHERE" or "HAVING" or "ON" or "SET" || grammarSuggestsColumns)
        {
            AddScopeColumns(items, scope, snapshots, wordPrefix, dialect, defaultSnapshot);
            AddRoutines(items, snapshots, wordPrefix, dialect);
            return items;
        }

        return null;
    }

    // ================================================================== Квалификатор

    private async Task<bool> CompleteQualifierAsync(
        List<CompletionItem> items, List<string> chain, QueryScope scope,
        List<SchemaSnapshot> snapshots, string wordPrefix, DbKind dialect,
        CompletionRequest request, CancellationToken ct)
    {
        if (chain.Count == 1)
        {
            var q = chain[0];

            // 1) Алиас или имя таблицы в цепочке областей видимости (включая коррелированные).
            TableRef? found = null;
            foreach (var s in scope.SelfAndParents())
            {
                found = s.Tables.FirstOrDefault(t => string.Equals(t.Alias, q, StringComparison.OrdinalIgnoreCase))
                        ?? s.Tables.FirstOrDefault(t => string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase));
                if (found is not null)
                    break;
            }
            if (found is not null)
            {
                var src = FindColumns(found, snapshots);
                if (src is not null)
                {
                    AddColumnList(items, src.Value, q, wordPrefix, dialect);
                    return true;
                }
                // Таблицы нет в кэше. Частый случай: «FROM schema.» — анализатор областей
                // видимости принимает начатое квалифицированное имя за таблицу «schema».
                // Поэтому не выходим сразу, а пробуем CTE и схему ниже; если и там пусто —
                // вернём false и отработает v0-эвристика.
            }

            // 2) CTE, ещё не упомянутый во FROM.
            if (scope.FindCte(q) is { } cte && cte.Columns.Count > 0)
            {
                AddColumnList(items, (cte.Name, SyntheticColumns(cte.Columns)), q, wordPrefix, dialect);
                return true;
            }

            // 3) Схема: объекты схемы.
            var schemaSnap = snapshots.FirstOrDefault(
                                 s => string.Equals(s.SchemaName, q, StringComparison.OrdinalIgnoreCase))
                             ?? await TryLoadSchemaAsync(request, q, ct);
            if (schemaSnap is not null)
            {
                foreach (var t in schemaSnap.Tables)
                    AddTableItem(items, t.Name, t.Schema, t.Type, t.Comment, wordPrefix,
                        Priority.ContextTable, aliasFactory: null, dialect);
                AddRoutines(items, [schemaSnap], wordPrefix, dialect);
                return true;
            }
            return false;
        }

        if (chain.Count == 2)
        {
            // «schema.table.» → колонки таблицы.
            var snap = snapshots.FirstOrDefault(
                           s => string.Equals(s.SchemaName, chain[0], StringComparison.OrdinalIgnoreCase))
                       ?? await TryLoadSchemaAsync(request, chain[0], ct);
            var table = snap?.Tables.FirstOrDefault(
                t => string.Equals(t.Name, chain[1], StringComparison.OrdinalIgnoreCase));
            if (table is not null)
            {
                AddColumnList(items, (table.Name, table.Columns), chain[1], wordPrefix, dialect);
                return true;
            }
        }
        return false;
    }

    /// <summary>Колонки источника + сниппет «q.* → колонки».</summary>
    private static void AddColumnList(
        List<CompletionItem> items, (string Display, IReadOnlyList<ColumnInfo> Columns) src,
        string qualifier, string wordPrefix, DbKind dialect)
    {
        foreach (var c in src.Columns)
            AddColumnItem(items, c, src.Display, wordPrefix, Priority.ScopeColumn, dialect);

        // Раскрытие t.*: вставка заменяет текст после «q.», поэтому первая колонка — без префикса.
        if (wordPrefix.Length == 0 && src.Columns.Count > 1)
        {
            var quoted = src.Columns.Select(c => SqlIdentifierQuoting.Quote(c.Name, dialect)).ToList();
            var text = quoted[0] + ", " + string.Join(", ", quoted.Skip(1).Select(c => qualifier + "." + c));
            items.Add(new CompletionItem
            {
                Label = $"{qualifier}.* → колонки",
                Kind = "snippet",
                InsertText = text,
                Detail = $"Раскрыть все колонки {src.Display}",
                SortPriority = Priority.StarExpand,
            });
        }
    }

    // ================================================================== Таблицы

    private async Task AddTablesAsync(
        List<CompletionItem> items, CompletionRequest request, QueryScope scope,
        List<SchemaSnapshot> snapshots, string wordPrefix, bool autoAlias, DbKind dialect, CancellationToken ct)
    {
        // Таблицы, уже задействованные в запросе: для FK-ранжирования и уникальности автоалиасов.
        var used = new List<TableInfo>();
        var takenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in scope.SelfAndParents())
        {
            foreach (var t in s.Tables)
            {
                if (t.Alias is not null)
                    takenAliases.Add(t.Alias);
                var info = FindTable(snapshots, t.Schema, t.Name);
                if (info is not null)
                    used.Add(info);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // CTE текущего запроса — «виртуальные таблицы» с наивысшим табличным приоритетом.
        foreach (var s in scope.SelfAndParents())
        {
            foreach (var cte in s.Ctes.Values)
            {
                if (!seen.Add("cte." + cte.Name))
                    continue;
                AddTableItem(items, cte.Name, "CTE", DbObjectType.Table, null, wordPrefix,
                    Priority.FkRelatedTable, autoAlias ? () => MakeAlias(cte.Name, takenAliases) : null, dialect);
            }
        }

        void Add(string name, string? schema, DbObjectType type, string? comment, IReadOnlyList<ForeignKeyInfo>? fks)
        {
            if (!seen.Add((schema ?? "") + "." + name))
                return;
            var related = IsFkRelated(name, schema, fks, used);
            AddTableItem(items, name, schema, type, comment, wordPrefix,
                related ? Priority.FkRelatedTable : Priority.ContextTable,
                autoAlias ? () => MakeAlias(name, takenAliases) : null, dialect);
        }

        foreach (var snap in snapshots)
        {
            foreach (var t in snap.Tables)
                Add(t.Name, t.Schema, t.Type, t.Comment, t.ForeignKeys);
        }

        // Кросс-схемный поиск по кэшу (может вернуть объекты других схем).
        try
        {
            foreach (var node in await _metadata.SearchAsync(request.DataSourceId, wordPrefix, 100, ct))
            {
                if (node.Type is DbObjectType.Table or DbObjectType.View or DbObjectType.MaterializedView)
                    Add(node.Name, node.Schema, node.Type, node.Comment, null);
            }
        }
        catch
        {
            // Поиск опционален.
        }

        // Схемы — как первая часть qualified-имени.
        foreach (var snap in snapshots)
        {
            var rank = HumpsMatcher.MatchRank(wordPrefix, snap.SchemaName);
            if (rank is null)
                continue;
            items.Add(new CompletionItem
            {
                Label = snap.SchemaName,
                Kind = "schema",
                InsertText = SqlIdentifierQuoting.Quote(snap.SchemaName, dialect),
                SortPriority = Priority.SchemaName + rank.Value,
            });
        }
    }

    private static void AddTableItem(
        List<CompletionItem> items, string name, string? detail, DbObjectType type, string? comment,
        string wordPrefix, int basePriority, Func<string>? aliasFactory, DbKind dialect)
    {
        var rank = HumpsMatcher.MatchRank(wordPrefix, name);
        if (rank is null)
            return;
        var insert = SqlIdentifierQuoting.Quote(name, dialect);
        if (aliasFactory is not null)
            insert = insert + " " + aliasFactory();
        items.Add(new CompletionItem
        {
            Label = name,
            Kind = type == DbObjectType.Table ? "table" : "view",
            InsertText = insert,
            Detail = detail,
            Documentation = comment,
            SortPriority = basePriority + rank.Value,
        });
    }

    /// <summary>Есть ли FK между кандидатом и уже используемыми таблицами (в любую сторону).</summary>
    private static bool IsFkRelated(
        string name, string? schema, IReadOnlyList<ForeignKeyInfo>? fks, List<TableInfo> used)
    {
        foreach (var u in used)
        {
            if (fks is not null && fks.Any(fk => SameTable(fk.ToSchema, fk.ToTable, u.Schema, u.Name)))
                return true;
            if (u.ForeignKeys.Any(fk => SameTable(fk.ToSchema, fk.ToTable, schema, name)))
                return true;
        }
        return false;
    }

    private static bool SameTable(string? schemaA, string nameA, string? schemaB, string nameB) =>
        string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase)
        && (schemaA is null || schemaB is null
            || string.Equals(schemaA, schemaB, StringComparison.OrdinalIgnoreCase));

    /// <summary>Автоалиас: первые буквы snake/camel-сегментов; при коллизии — числовой суффикс.</summary>
    internal static string MakeAlias(string table, HashSet<string> taken)
    {
        var initials = new System.Text.StringBuilder();
        var newSegment = true;
        foreach (var ch in table)
        {
            if (ch is '_' or '$' or '#' || char.IsDigit(ch))
            {
                newSegment = true;
                continue;
            }
            if (newSegment && char.IsLetter(ch))
            {
                initials.Append(char.ToLowerInvariant(ch));
                newSegment = false;
            }
            else if (char.IsUpper(ch))
            {
                initials.Append(char.ToLowerInvariant(ch));
            }
        }
        var baseAlias = initials.Length > 0 ? initials.ToString() : "t";
        var alias = baseAlias;
        var n = 2;
        while (taken.Contains(alias))
            alias = baseAlias + n++;
        taken.Add(alias);
        return alias;
    }

    // ================================================================== JOIN … ON по FK

    /// <summary>Готовые условия соединения по FK между «текущей» (последней перед ON) и остальными таблицами.</summary>
    private static void AddFkJoinSnippets(
        List<CompletionItem> items, QueryScope scope, List<SchemaSnapshot> snapshots,
        int caret, string wordPrefix, DbKind dialect)
    {
        var refs = scope.Tables.Where(t => t.RefStart < caret).OrderBy(t => t.RefStart).ToList();
        if (refs.Count < 2)
            return;
        var current = refs[^1];
        var others = refs.Take(refs.Count - 1).ToList();

        string RefText(TableRef r) => r.Alias ?? SqlIdentifierQuoting.Quote(r.Name, dialect);

        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in snapshots.SelectMany(s => s.Tables).SelectMany(t => t.ForeignKeys))
        {
            foreach (var other in others)
            {
                TableRef? fromRef = null, toRef = null;
                if (Matches(fk.FromSchema, fk.FromTable, current) && Matches(fk.ToSchema, fk.ToTable, other))
                {
                    fromRef = current;
                    toRef = other;
                }
                else if (Matches(fk.FromSchema, fk.FromTable, other) && Matches(fk.ToSchema, fk.ToTable, current))
                {
                    fromRef = other;
                    toRef = current;
                }
                if (fromRef is null || toRef is null)
                    continue;

                var parts = new List<string>();
                for (var k = 0; k < fk.FromColumns.Count && k < fk.ToColumns.Count; k++)
                {
                    parts.Add($"{RefText(fromRef)}.{SqlIdentifierQuoting.Quote(fk.FromColumns[k], dialect)}"
                              + $" = {RefText(toRef)}.{SqlIdentifierQuoting.Quote(fk.ToColumns[k], dialect)}");
                }
                if (parts.Count == 0)
                    continue;
                var text = string.Join(" AND ", parts);
                if (wordPrefix.Length > 0 && !text.StartsWith(wordPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!added.Add(text))
                    continue;
                items.Add(new CompletionItem
                {
                    Label = text,
                    Kind = "snippet",
                    InsertText = text,
                    Detail = "условие по FK " + fk.ConstraintName,
                    SortPriority = Priority.FkJoinSnippet,
                });
            }
        }

        static bool Matches(string schema, string table, TableRef r) =>
            string.Equals(table, r.Name, StringComparison.OrdinalIgnoreCase)
            && (r.Schema is null || string.Equals(schema, r.Schema, StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================== Колонки

    /// <summary>Колонки таблиц цепочки областей: текущий scope выше родительских; fallback — вся схема.</summary>
    private static void AddScopeColumns(
        List<CompletionItem> items, QueryScope scope, List<SchemaSnapshot> snapshots,
        string wordPrefix, DbKind dialect, SchemaSnapshot? fallbackSnapshot)
    {
        var level = 0;
        var any = false;
        foreach (var s in scope.SelfAndParents())
        {
            foreach (var t in s.Tables)
            {
                var src = FindColumns(t, snapshots);
                if (src is null)
                    continue;
                any = true;
                var priority = Priority.ScopeColumn + Math.Min(level, 2) * 2;
                foreach (var c in src.Value.Columns)
                    AddColumnItem(items, c, src.Value.Display, wordPrefix, priority, dialect);
            }
            level++;
        }
        if (!any && fallbackSnapshot is not null)
        {
            foreach (var t in fallbackSnapshot.Tables)
            {
                foreach (var c in t.Columns)
                    AddColumnItem(items, c, t.Name, wordPrefix, Priority.OtherColumn, dialect);
            }
        }
    }

    // ================================================================== Функции и процедуры

    /// <summary>
    /// Функции и процедуры закэшированных схем. Перегрузки (одно имя, разные аргументы)
    /// сворачиваются в один элемент: в списке подсказок десяток одинаковых меток бесполезен,
    /// а сигнатура первой из них уже показывает, как вызов выглядит.
    /// </summary>
    private static void AddRoutines(
        List<CompletionItem> items, List<SchemaSnapshot> snapshots, string wordPrefix, DbKind dialect)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            foreach (var routine in snapshot.Routines)
            {
                var rank = HumpsMatcher.MatchRank(wordPrefix, routine.Name);
                if (rank is null)
                    continue;
                if (!seen.Add(routine.Schema + "." + routine.Name))
                    continue;
                items.Add(CompletionEngine.RoutineItem(routine, Priority.Routine + rank.Value, dialect));
            }
        }
    }

    private static void AddSelectAliases(List<CompletionItem> items, QueryScope scope, string wordPrefix, DbKind dialect)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in scope.SelectItems)
        {
            if (it.OutputName is null || !seen.Add(it.OutputName))
                continue;
            var rank = HumpsMatcher.MatchRank(wordPrefix, it.OutputName);
            if (rank is null)
                continue;
            items.Add(new CompletionItem
            {
                Label = it.OutputName,
                Kind = "alias",
                InsertText = SqlIdentifierQuoting.Quote(it.OutputName, dialect),
                Detail = "алиас SELECT-списка",
                SortPriority = Priority.SelectAlias + rank.Value,
            });
        }
    }

    private static void AddColumnItem(
        List<CompletionItem> items, ColumnInfo c, string ownerDisplay,
        string wordPrefix, int basePriority, DbKind dialect)
    {
        var rank = HumpsMatcher.MatchRank(wordPrefix, c.Name);
        if (rank is null)
            return;
        items.Add(new CompletionItem
        {
            Label = c.Name,
            Kind = "column",
            InsertText = SqlIdentifierQuoting.Quote(c.Name, dialect),
            Detail = string.IsNullOrEmpty(c.DataType)
                ? $"{ownerDisplay}.{c.Name}"
                : $"{ownerDisplay}.{c.Name} : {c.DataType}",
            Documentation = c.Comment,
            SortPriority = basePriority + rank.Value,
        });
    }

    // ================================================================== Метаданные

    /// <summary>Снапшоты: схема по умолчанию + все загруженные (без дублей).</summary>
    private async Task<List<SchemaSnapshot>> GetSnapshotsAsync(
        CompletionRequest request, SchemaSnapshot? defaultSnapshot, CancellationToken ct)
    {
        var list = new List<SchemaSnapshot>();
        if (defaultSnapshot is not null)
            list.Add(defaultSnapshot);
        try
        {
            foreach (var s in await _metadata.GetLoadedSchemasAsync(request.DataSourceId, ct))
            {
                if (!list.Any(x => string.Equals(x.SchemaName, s.SchemaName, StringComparison.OrdinalIgnoreCase)))
                    list.Add(s);
            }
        }
        catch
        {
            // Кэш может быть недоступен — работаем с тем, что есть.
        }
        return list;
    }

    private async Task<SchemaSnapshot?> TryLoadSchemaAsync(CompletionRequest request, string schema, CancellationToken ct)
    {
        try
        {
            return await _metadata.GetSchemaAsync(request.DataSourceId, schema, ct);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Колонки ссылки: derived/CTE — из модели, иначе — из кэша метаданных.</summary>
    private static (string Display, IReadOnlyList<ColumnInfo> Columns)? FindColumns(
        TableRef tref, List<SchemaSnapshot> snapshots)
    {
        if (tref.Columns is not null)
            return (tref.Alias ?? tref.Name, SyntheticColumns(tref.Columns));
        var table = FindTable(snapshots, tref.Schema, tref.Name);
        return table is null ? null : (tref.Alias ?? table.Name, table.Columns);
    }

    private static IReadOnlyList<ColumnInfo> SyntheticColumns(IReadOnlyList<string> names) =>
        names.Select((n, idx) => new ColumnInfo { Name = n, DataType = "", OrdinalPosition = idx + 1 }).ToList();

    private static TableInfo? FindTable(List<SchemaSnapshot> snapshots, string? schema, string name)
    {
        foreach (var snap in snapshots)
        {
            if (schema is not null
                && !string.Equals(snap.SchemaName, schema, StringComparison.OrdinalIgnoreCase))
                continue;
            var table = snap.Tables.FirstOrDefault(
                t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (table is not null)
                return table;
        }
        return null;
    }
}
