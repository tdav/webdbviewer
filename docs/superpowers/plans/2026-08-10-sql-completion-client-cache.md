# Клиентский кэш schema-map для SQL Code Completion — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Убрать сетевой round-trip из каждого нажатия клавиши в SQL-редакторе: снапшот схемы уходит на клиент один раз, фильтрация префикса идёт локально, серверный ANTLR-движок уточняет результат в фоне.

**Architecture:** Новый `GET /api/completion/schema-map` отдаёт содержимое существующего `MetadataCache` компактным JSON с ETag. Клиентский модуль держит снапшот в памяти и выдаёт варианты мгновенно; тот же completion source параллельно запрашивает сервер и на втором проходе объединяет ответы (серверные первыми, локальные — только отсутствующие). Плюс кнопка инвалидации метаданных, `RESULT_CACHE` в словарных запросах Oracle и замеры латентности.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, xUnit; CodeMirror 6 (`@codemirror/autocomplete`, `@codemirror/lang-sql`), esbuild; Oracle.ManagedDataAccess.Core, Npgsql.

**Спека:** `docs/superpowers/specs/2026-08-10-sql-code-completion-design.md`

## Global Constraints

- Solution: `WebDbViewerSol.slnx`. Целевой фреймворк — `net10.0`.
- Приватные поля класса — **без префикса `_`** в новом коде: `private readonly IFooService fooService;`, обращение через `this.fooService`. Существующий код с `_` не переписывать.
- Комментарии в коде и commit-сообщения — на английском; текст UI, тултипы и сообщения об ошибках — на русском.
- Air-gapped: никаких новых npm/NuGet-зависимостей и никаких внешних CDN.
- **Параллельная работа агентов в одном рабочем дереве:**
  - `git add -A` и `git add .` запрещены — индексировать только точные пути файлов, перечисленные в задаче (не папки).
  - Сборка и тесты — в изолированный выходной каталог, чтобы не драться за `bin/`: `-o "$env:TEMP\wdb-taskN"` (N — номер задачи).
  - Свой порт при запуске приложения: `--urls http://localhost:51NN` (NN — номер задачи).
  - Убивать только собственный PID процесса; `taskkill /F /IM dotnet.exe` запрещён — снимет серверы других агентов.
- Фронтенд-бандл `src/WebDbViewer.Web/wwwroot/js/*.js` **никто из агентов не собирает и не коммитит** — это делает основная сессия один раз между батчами (см. «Барьер»).
- Существующие тесты не переписываются; чинятся только если сломаны изменениями этой задачи.

---

# Батч 1 — реализация (задачи 1–5, запускаются параллельно)

---

### Task 1: Endpoint снапшота схемы `schema-map`

**Files:**
- Create: `src/WebDbViewer.Web/Api/SchemaMapDto.cs`
- Modify: `src/WebDbViewer.Web/Api/CompletionEndpoints.cs`
- Test: `tests/WebDbViewer.Tests.Unit/Completion/SchemaMapDtoTests.cs`
- **Не трогать** `frontend/src/editor.js` — файл принадлежит Task 3; клиентский вызов warmup удаляет она.

**Interfaces:**
- Consumes: `IMetadataCache.GetSchemaAsync(Guid, string, CancellationToken) → Task<SchemaSnapshot>`; `IDataSourceStore.GetAsync(Guid, CancellationToken) → Task<DataSourceConfig?>`; `CompletionEndpoints.DefaultSchemaFor(DataSourceConfig, string?) → string?`. Модели: `SchemaSnapshot { SchemaName, Tables, Routines, LoadedAt, VersionHash }`, `TableInfo { Schema, Name, Type, Columns, ForeignKeys, PrimaryKeyColumns, Comment }`, `ColumnInfo { Name, DataType, IsNullable, IsPrimaryKey, Comment, OrdinalPosition }`, `RoutineInfo { Schema, Name, Type, ReturnType, ArgumentsSignature, Comment }`, `DbObjectType { Table, View, MaterializedView, Function, Procedure, … }`.
- Produces: HTTP-контракт `GET /api/completion/schema-map?dsId={guid}&schema={name}` → `200` с телом (см. Step 1), `304` при совпадении `If-None-Match`, `204` если схема неизвестна или интроспекция упала, `404` если датасорса нет. Публичные C#-члены: `SchemaMapDto.From(SchemaSnapshot)`, `SchemaMapDto.ETagFor(SchemaSnapshot)`, `SchemaMapDto.IsNotModified(string?, string?)`, константы `SchemaMapDto.MaxTables = 2000`, `SchemaMapDto.MaxColumns = 50000`.
- Ломает: `POST /api/completion/warmup` удаляется. Метод `IMetadataCache.WarmupAsync` **остаётся** — он используется тестами и задачей 4.

- [ ] **Step 1: Написать падающий тест на построение DTO**

Создать `tests/WebDbViewer.Tests.Unit/Completion/SchemaMapDtoTests.cs`:

```csharp
using System.Text.Json;
using WebDbViewer.Core;
using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Unit.Completion;

public class SchemaMapDtoTests
{
    private static SchemaSnapshot Snapshot(int tables, int columnsPerTable, string? versionHash = "v1") => new()
    {
        SchemaName = "public",
        Tables = Enumerable.Range(0, tables).Select(i => new TableInfo
        {
            Schema = "public",
            Name = "t" + i,
            Type = DbObjectType.Table,
            Columns = Enumerable.Range(0, columnsPerTable).Select(c => new ColumnInfo
            {
                Name = "c" + c,
                DataType = "text",
                OrdinalPosition = c + 1,
                IsNullable = true,
            }).ToList(),
        }).ToList(),
        LoadedAt = DateTimeOffset.UtcNow,
        VersionHash = versionHash,
    };

    [Fact]
    public void From_UsesShortKeys()
    {
        var dto = SchemaMapDto.From(SnapshotWithComment());
        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"n\":\"users\"", json);
        Assert.Contains("\"t\":\"table\"", json);
        Assert.Contains("\"pk\":true", json);
        Assert.Contains("\"cm\":\"Пользователи\"", json);
    }

    [Fact]
    public void From_OmitsEmptyComment()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: 1, columnsPerTable: 1));
        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("\"cm\"", json);
    }

    [Fact]
    public void From_DropsColumnsWhenTooManyTables()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: SchemaMapDto.MaxTables + 1, columnsPerTable: 1));

        Assert.True(dto.Partial);
        Assert.All(dto.Tables, t => Assert.Empty(t.Columns));
    }

    [Fact]
    public void From_DropsColumnsWhenTooManyColumns()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: 100, columnsPerTable: 501)); // 50 100 > 50 000

        Assert.True(dto.Partial);
        Assert.All(dto.Tables, t => Assert.Empty(t.Columns));
    }

    [Fact]
    public void From_KeepsColumnsAtThreshold()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: SchemaMapDto.MaxTables, columnsPerTable: 1));

        Assert.False(dto.Partial);
        Assert.All(dto.Tables, t => Assert.Single(t.Columns));
    }

    [Theory]
    [InlineData("\"v1\"", true)]
    [InlineData("\"v2\"", false)]
    [InlineData(null, false)]
    public void IsNotModified_ComparesETag(string? ifNoneMatch, bool expected)
    {
        var etag = SchemaMapDto.ETagFor(Snapshot(1, 1));

        Assert.Equal(expected, SchemaMapDto.IsNotModified(etag, ifNoneMatch));
    }

    [Fact]
    public void IsNotModified_FalseWhenNoVersionHash()
    {
        var etag = SchemaMapDto.ETagFor(Snapshot(1, 1, versionHash: null));

        Assert.Null(etag);
        Assert.False(SchemaMapDto.IsNotModified(etag, "\"anything\""));
    }

    private static SchemaSnapshot SnapshotWithComment() => new()
    {
        SchemaName = "public",
        Tables =
        [
            new TableInfo
            {
                Schema = "public",
                Name = "users",
                Type = DbObjectType.Table,
                Comment = "Пользователи",
                Columns = [new ColumnInfo { Name = "id", DataType = "bigint", OrdinalPosition = 1, IsPrimaryKey = true }],
                PrimaryKeyColumns = ["id"],
            },
        ],
        LoadedAt = DateTimeOffset.UtcNow,
        VersionHash = "v1",
    };
}
```

- [ ] **Step 2: Запустить тест — убедиться, что он падает**

```bash
dotnet test tests/WebDbViewer.Tests.Unit/WebDbViewer.Tests.Unit.csproj -o "$env:TEMP\wdb-task1" --filter FullyQualifiedName~SchemaMapDtoTests
```

Ожидается: ошибка компиляции — `SchemaMapDto` не существует.

- [ ] **Step 3: Создать `src/WebDbViewer.Web/Api/SchemaMapDto.cs`**

```csharp
using System.Text.Json.Serialization;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>Column of a schema snapshot sent to the browser. Keys are short: the whole schema travels over the wire.</summary>
public sealed record SchemaMapColumn(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("d")] string DataType,
    [property: JsonPropertyName("pk")] bool IsPrimaryKey,
    [property: JsonPropertyName("nl")] bool IsNullable,
    [property: JsonPropertyName("cm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>Table or view of a schema snapshot.</summary>
public sealed record SchemaMapTable(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("c")] IReadOnlyList<SchemaMapColumn> Columns,
    [property: JsonPropertyName("cm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>Routine of a schema snapshot.</summary>
public sealed record SchemaMapRoutine(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("t")] string Type,
    [property: JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Signature,
    [property: JsonPropertyName("cm"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Comment);

/// <summary>Schema snapshot for client-side completion.</summary>
public sealed record SchemaMapResponse(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("partial")] bool Partial,
    [property: JsonPropertyName("tables")] IReadOnlyList<SchemaMapTable> Tables,
    [property: JsonPropertyName("routines")] IReadOnlyList<SchemaMapRoutine> Routines);

/// <summary>
/// Projection of <see cref="SchemaSnapshot"/> onto the wire format of /api/completion/schema-map.
/// Pure functions only — the endpoint stays a thin wrapper and this stays unit-testable.
/// </summary>
public static class SchemaMapDto
{
    /// <summary>Above these sizes columns are dropped: a full snapshot would cost megabytes per editor open.</summary>
    public const int MaxTables = 2000;
    public const int MaxColumns = 50000;

    public static SchemaMapResponse From(SchemaSnapshot snapshot)
    {
        var totalColumns = snapshot.Tables.Sum(t => t.Columns.Count);
        var partial = snapshot.Tables.Count > MaxTables || totalColumns > MaxColumns;

        var tables = snapshot.Tables
            .Select(t => new SchemaMapTable(
                t.Name,
                TypeName(t.Type),
                partial ? [] : t.Columns.Select(Column).ToList(),
                Trimmed(t.Comment)))
            .ToList();

        var routines = snapshot.Routines
            .Select(r => new SchemaMapRoutine(
                r.Name,
                r.Type == DbObjectType.Procedure ? "procedure" : "function",
                Trimmed(r.ArgumentsSignature),
                Trimmed(r.Comment)))
            .ToList();

        return new SchemaMapResponse(snapshot.SchemaName, partial, tables, routines);
    }

    /// <summary>ETag from the snapshot version; null when the provider does not report one.</summary>
    public static string? ETagFor(SchemaSnapshot snapshot) =>
        string.IsNullOrEmpty(snapshot.VersionHash) ? null : "\"" + snapshot.VersionHash + "\"";

    /// <summary>True when the client already holds this exact snapshot.</summary>
    public static bool IsNotModified(string? etag, string? ifNoneMatch)
    {
        if (etag is null || string.IsNullOrWhiteSpace(ifNoneMatch))
            return false;
        foreach (var candidate in ifNoneMatch.Split(','))
        {
            var value = candidate.Trim();
            if (value.StartsWith("W/", StringComparison.Ordinal))
                value = value[2..];
            if (string.Equals(value, etag, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static SchemaMapColumn Column(ColumnInfo c) =>
        new(c.Name, c.DataType, c.IsPrimaryKey, c.IsNullable, Trimmed(c.Comment));

    private static string TypeName(DbObjectType type) => type switch
    {
        DbObjectType.View => "view",
        DbObjectType.MaterializedView => "mview",
        _ => "table",
    };

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

- [ ] **Step 4: Запустить тест — убедиться, что он проходит**

```bash
dotnet test tests/WebDbViewer.Tests.Unit/WebDbViewer.Tests.Unit.csproj -o "$env:TEMP\wdb-task1" --filter FullyQualifiedName~SchemaMapDtoTests
```

Ожидается: PASS, 7 тестов.

- [ ] **Step 5: Заменить warmup-endpoint на schema-map в `CompletionEndpoints.cs`**

В `MapCompletionApi` заменить строку с warmup:

```csharp
        app.MapPost("/api/completion", CompleteAsync).RequireAuthorization();
        app.MapPost("/api/completion/signature", SignatureAsync).RequireAuthorization();
        app.MapGet("/api/completion/schema-map", SchemaMapAsync).RequireAuthorization();
        return app;
```

Удалить целиком: запись `CompletionWarmupRequest` и метод `WarmupAsync` вместе с его XML-комментарием. Добавить вместо него:

```csharp
    /// <summary>
    /// Снапшот схемы для клиентского автодополнения. Он же прогрев кэша: построение ответа
    /// заполняет MetadataCache, отдельный warmup-запрос не нужен.
    /// 204 — схему определить не удалось или интроспекция упала: редактор просто работает
    /// без локального кэша, подсказки идут с сервера как раньше.
    /// </summary>
    private static async Task<IResult> SchemaMapAsync(
        Guid dsId,
        string? schema,
        HttpContext http,
        IDataSourceStore dataSourceStore,
        IMetadataCache metadata,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (dsId == Guid.Empty)
            return Results.BadRequest(new { error = "Не задан датасорс." });

        var config = await dataSourceStore.GetAsync(dsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var schemaName = DefaultSchemaFor(config, schema);
        if (string.IsNullOrWhiteSpace(schemaName))
            return Results.NoContent();

        SchemaSnapshot snapshot;
        try
        {
            snapshot = await metadata.GetSchemaAsync(dsId, schemaName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggerFactory.CreateLogger(typeof(CompletionEndpoints))
                .LogWarning(ex, "Снапшот схемы {Schema} датасорса {DataSourceId} получить не удалось", schemaName, dsId);
            return Results.NoContent();
        }

        var etag = SchemaMapDto.ETagFor(snapshot);
        if (SchemaMapDto.IsNotModified(etag, http.Request.Headers.IfNoneMatch))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        if (etag is not null)
            http.Response.Headers.ETag = etag;
        return Results.Json(SchemaMapDto.From(snapshot));
    }
```

- [ ] **Step 6: Собрать и убедиться, что весь тестовый проект зелёный**

```bash
dotnet build WebDbViewerSol.slnx -o "$env:TEMP\wdb-task1"
```

Ожидается: Build succeeded, 0 errors. Затем:

```bash
dotnet test tests/WebDbViewer.Tests.Unit/WebDbViewer.Tests.Unit.csproj -o "$env:TEMP\wdb-task1"
```

Ожидается: все тесты PASS. Если падает тест, ссылающийся на удалённый warmup-endpoint, — починить его; `IMetadataCache.WarmupAsync` при этом не трогать.

- [ ] **Step 7: Коммит**

```bash
git add src/WebDbViewer.Web/Api/SchemaMapDto.cs src/WebDbViewer.Web/Api/CompletionEndpoints.cs tests/WebDbViewer.Tests.Unit/Completion/SchemaMapDtoTests.cs
git commit -m "feat: schema-map endpoint for client-side completion cache"
```

---

### Task 2: Клиентский модуль снапшота схемы

**Files:**
- Create: `frontend/src/completion-schema.js`

**Interfaces:**
- Consumes: `GET /api/completion/schema-map?dsId=&schema=` (Task 1): `{schema, partial, tables:[{n,t,c:[{n,d,pk,nl,cm}],cm}], routines:[{n,t,s,cm}]}`, ETag + `If-None-Match`, `204` без тела.
- Produces: глобальный объект `window.WebDbCompletion` со строго такими членами (Task 3 и Task 4 зовут именно их):
  - `load(dsId, schema) → Promise<void>` — загрузить снапшот в кэш; ошибки проглатываются.
  - `reset(dsId, schema) → void` — выбросить запись кэша (сбрасывает и ETag).
  - `localCompletions({ text, pos, dsId, schema, dialect }) → { from: number, options: Array } | null` — варианты из кэша; `null`, если снапшота нет.
  - `stats() → { count: number, p50: number, p95: number }` — тайминги локальной фильтрации в мс.
  - Элемент `options[i]`: `{ label, type, apply, detail, info, boost }` — формат CodeMirror.

- [ ] **Step 1: Создать `frontend/src/completion-schema.js`**

```js
// Снапшот схемы для локального автодополнения.
// Загружается один раз на пару (датасорс, схема) и живёт в памяти вкладки:
// фильтрация префикса по нему идёт без сети, поэтому попап открывается мгновенно.
// Серверный движок (editor.js) уточняет результат следом — здесь только быстрый черновик.

const cache = new Map();    // ключ -> { data, etag }
const inflight = new Map(); // ключ -> Promise
const timings = [];         // длительности localCompletions, мс

const KEY = (dsId, schema) => (dsId || '') + '/' + (schema || '');

// --- Загрузка ---

export async function load(dsId, schema) {
  if (!dsId) return;
  const key = KEY(dsId, schema);
  if (inflight.has(key)) return inflight.get(key);

  const promise = (async () => {
    const url = '/api/completion/schema-map?dsId=' + encodeURIComponent(dsId)
      + (schema ? '&schema=' + encodeURIComponent(schema) : '');
    const headers = {};
    const known = cache.get(key);
    if (known && known.etag) headers['If-None-Match'] = known.etag;
    try {
      const res = await fetch(url, { headers });
      if (res.status === 304) return;          // снапшот в кэше актуален
      if (!res.ok || res.status === 204) return; // без локального кэша работаем как раньше
      const data = await res.json();
      cache.set(key, { data, etag: res.headers.get('ETag') });
    } catch (e) {
      // Сеть недоступна — подсказки просто пойдут только с сервера.
    } finally {
      inflight.delete(key);
    }
  })();

  inflight.set(key, promise);
  return promise;
}

export function reset(dsId, schema) {
  cache.delete(KEY(dsId, schema));
}

export function stats() {
  if (!timings.length) return { count: 0, p50: 0, p95: 0 };
  const sorted = [...timings].sort((a, b) => a - b);
  const at = (q) => sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
  return { count: sorted.length, p50: at(0.5), p95: at(0.95) };
}

// --- Правила, общие с сервером ---

// Порт SqlIdentifierQuoting.Quote: кавычки только если имя без них сменит смысл.
const PG_PLAIN = /^[a-z_][a-z0-9_$]*$/;
const ORACLE_PLAIN = /^[A-Z][A-Z0-9_$#]*$/;

function isOracle(dialect) {
  return (dialect || '').toLowerCase().includes('oracle');
}

function quote(identifier, dialect) {
  if (!identifier) return identifier;
  const plain = isOracle(dialect) ? ORACLE_PLAIN.test(identifier) : PG_PLAIN.test(identifier);
  return plain ? identifier : '"' + identifier.replace(/"/g, '""') + '"';
}

// Порт SemanticCompleter.MakeAlias: первые буквы snake/camel-сегментов, при коллизии — цифра.
// Расхождение с серверным правилом — дефект: пользователь получил бы разный текст
// в зависимости от того, успел ли прийти ответ сервера.
export function makeAlias(table, taken) {
  let initials = '';
  let newSegment = true;
  for (const ch of table) {
    if (ch === '_' || ch === '$' || ch === '#' || (ch >= '0' && ch <= '9')) {
      newSegment = true;
      continue;
    }
    const isLetter = /\p{L}/u.test(ch);
    if (newSegment && isLetter) {
      initials += ch.toLowerCase();
      newSegment = false;
    } else if (isLetter && ch === ch.toUpperCase() && ch !== ch.toLowerCase()) {
      initials += ch.toLowerCase();
    }
  }
  const base = initials.length ? initials : 't';
  let alias = base;
  let n = 2;
  while (taken.has(alias)) alias = base + n++;
  taken.add(alias);
  return alias;
}

// Приоритеты повторяют SemanticCompleter.Priority; boost в CodeMirror — «чем больше, тем выше»,
// поэтому знак инвертируется. Локальный вариант на единицу ниже серверного аналога:
// при совпадении сервер должен побеждать.
const PRIORITY = { scopeColumn: 5, contextTable: 8, otherColumn: 10, routine: 12 };
const boostOf = (priority) => -(priority + 1);

// --- Разбор контекста ---

const ALIAS_STOP = new Set([
  'where', 'on', 'join', 'inner', 'left', 'right', 'full', 'cross', 'natural', 'lateral',
  'group', 'order', 'having', 'limit', 'offset', 'fetch', 'set', 'values', 'using',
  'union', 'except', 'intersect', 'returning', 'window', 'for',
]);

/// Текст statement'а, внутри которого стоит каретка, и позиция каретки в нём.
function currentStatement(text, pos) {
  const before = text.slice(0, pos);
  const start = before.lastIndexOf(';') + 1;
  return { text: text.slice(start), caret: pos - start };
}

/// Таблицы и их алиасы из FROM/JOIN/UPDATE/INTO текущего statement.
function tableRefs(statement) {
  const re = /\b(?:from|join|update|into)\s+((?:[\w$#]+|"[^"]+")(?:\s*\.\s*(?:[\w$#]+|"[^"]+"))?)(?:\s+(?:as\s+)?([a-zA-Z][\w$#]*))?/gi;
  const refs = [];
  let m;
  while ((m = re.exec(statement)) !== null) {
    const parts = m[1].split('.').map((p) => p.trim().replace(/^"|"$/g, ''));
    const name = parts[parts.length - 1];
    const alias = m[2] && !ALIAS_STOP.has(m[2].toLowerCase()) ? m[2] : null;
    refs.push({ name, alias });
  }
  return refs;
}

const TABLE_CONTEXT = new Set(['from', 'join', 'into', 'update', 'table', 'lateral']);

// --- Построение вариантов ---

export function localCompletions({ text, pos, dsId, schema, dialect }) {
  const entry = cache.get(KEY(dsId, schema));
  if (!entry) return null;

  const started = performance.now();
  const snapshot = entry.data;
  const stmt = currentStatement(text, pos);
  const before = stmt.text.slice(0, stmt.caret);

  const word = /[\w$#]*$/.exec(before)[0];
  const from = pos - word.length;
  const qualifier = /([\w$#]+|"[^"]+")\s*\.\s*[\w$#]*$/.exec(before);
  const prevWord = (/([a-zA-Z]+)\s+[\w$#]*$/.exec(before) || [])[1];

  const refs = tableRefs(stmt.text);
  const options = [];

  const tableByName = new Map();
  for (const t of snapshot.tables) tableByName.set(t.n.toLowerCase(), t);

  if (qualifier) {
    // «alias.» или «table.» — колонки соответствующей таблицы и ничего больше.
    const q = qualifier[1].replace(/^"|"$/g, '').toLowerCase();
    const ref = refs.find((r) => (r.alias || '').toLowerCase() === q)
      || refs.find((r) => r.name.toLowerCase() === q);
    const table = tableByName.get((ref ? ref.name : q).toLowerCase());
    if (table) pushColumns(options, table, PRIORITY.scopeColumn, dialect);
  } else if (prevWord && TABLE_CONTEXT.has(prevWord.toLowerCase())) {
    // Позиция имени таблицы: только таблицы и вью, с автоалиасом.
    const taken = new Set(refs.map((r) => r.alias).filter(Boolean).map((a) => a.toLowerCase()));
    const autoAlias = prevWord.toLowerCase() !== 'into' && prevWord.toLowerCase() !== 'update'
      && prevWord.toLowerCase() !== 'table';
    pushTables(options, snapshot.tables, dialect, autoAlias ? taken : null);
  } else {
    // Общий случай: колонки таблиц текущего statement, затем таблицы и функции.
    for (const ref of refs) {
      const table = tableByName.get(ref.name.toLowerCase());
      if (table) pushColumns(options, table, PRIORITY.scopeColumn, dialect);
    }
    pushTables(options, snapshot.tables, dialect, null);
    pushRoutines(options, snapshot.routines, dialect);
  }

  timings.push(performance.now() - started);
  if (timings.length > 500) timings.shift();
  return options.length ? { from, options } : null;
}

function pushColumns(options, table, priority, dialect) {
  for (const c of table.c || []) {
    options.push({
      label: c.n,
      type: 'property',
      apply: quote(c.n, dialect),
      detail: columnDetail(c),
      info: c.cm || undefined,
      boost: boostOf(priority),
    });
  }
}

function columnDetail(c) {
  const parts = [c.d];
  if (c.pk) parts.push('PK');
  if (!c.nl) parts.push('NOT NULL');
  return parts.join(' · ');
}

function pushTables(options, tables, dialect, takenAliases) {
  for (const t of tables) {
    let apply = quote(t.n, dialect);
    if (takenAliases) apply = apply + ' ' + makeAlias(t.n, takenAliases);
    options.push({
      label: t.n,
      type: 'class',
      apply,
      detail: t.t === 'table' ? undefined : t.t,
      info: t.cm || undefined,
      boost: boostOf(PRIORITY.contextTable),
    });
  }
}

function pushRoutines(options, routines, dialect) {
  for (const r of routines || []) {
    options.push({
      label: r.n,
      type: 'function',
      apply: quote(r.n, dialect) + '(',
      detail: r.s || undefined,
      info: r.cm || undefined,
      boost: boostOf(PRIORITY.routine),
    });
  }
}

// Публичный API для editor.js и обработчика кнопки «Обновить метаданные».
window.WebDbCompletion = { load, reset, localCompletions, stats, makeAlias };
```

- [ ] **Step 2: Проверить, что модуль собирается**

Добавить его во временную точку входа не нужно — esbuild возьмёт его как импорт из `editor.js` (Task 3). Достаточно синтаксической проверки:

```bash
node --input-type=module --eval "await import('./frontend/src/completion-schema.js').catch(e => { if (!String(e).includes('window is not defined')) { console.error(e); process.exit(1); } })"
```

Ожидается: команда завершается с кодом 0 (ошибка `window is not defined` ожидаема — модуль рассчитан на браузер, синтаксис при этом уже разобран).

- [ ] **Step 3: Коммит**

```bash
git add frontend/src/completion-schema.js
git commit -m "feat: client-side schema snapshot module for completion"
```

---

### Task 3: Двухфазный источник автодополнения

**Files:**
- Modify: `frontend/src/editor.js`

**Interfaces:**
- Consumes: `window.WebDbCompletion.load / reset / localCompletions` (Task 2, сигнатуры см. там); `POST /api/completion` (без изменений); endpoint `POST /api/completion/warmup` **удалён** Task 1 — все его вызовы должны исчезнуть.
- Produces: поведение редактора; внешних API не добавляет.

- [ ] **Step 1: Заменить прогрев на загрузку снапшота**

В `frontend/src/editor.js` заменить функцию `warmupCompletion` (и все три её вызова оставить по именам — переименовать саму функцию):

```js
// --- Загрузка снапшота схемы ---
// Снапшот заменяет прежний прогрев кэша: его построение и есть прогрев,
// а на клиенте он даёт мгновенные подсказки без сети.
let lastSchemaLoad = null;

function loadSchemaMap(dsId, schema) {
  if (!dsId) return;
  const key = dsId + '/' + (schema || '');
  if (key === lastSchemaLoad) return; // повторная загрузка той же схемы не нужна
  lastSchemaLoad = key;
  window.WebDbCompletion.load(dsId, schema);
}
```

Заменить три вызова `warmupCompletion(...)` на `loadSchemaMap(...)`: в `initEditor` (после создания view), в обработчике смены схемы и в обработчике смены датасорса. Аргументы не меняются.

Добавить импорт в начало файла, после существующих импортов:

```js
import './completion-schema.js';
```

- [ ] **Step 2: Сделать источник двухфазным**

Заменить функцию `makeCompletionSource` целиком на:

```js
function makeCompletionSource(textarea) {
  let timer = null;
  let controller = null;
  let pendingResolve = null;
  // Ответ сервера для конкретной каретки: ключ «позиция|текст» отсекает
  // ответ, пришедший к уже изменившемуся запросу.
  let serverAnswer = null;

  function dropPending() {
    if (timer) { clearTimeout(timer); timer = null; }
    if (pendingResolve) { pendingResolve(null); pendingResolve = null; }
  }

  const answerKey = (context) => context.pos + '|' + context.state.doc.toString();

  async function fetchCompletions(context, word) {
    if (controller) controller.abort(); // отмена устаревшего запроса
    controller = new AbortController();
    try {
      const res = await fetch('/api/completion', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          dsId: textarea.dataset.dsId,
          sql: context.state.doc.toString(),
          caretOffset: context.pos,
          defaultSchema: currentSchema(),
        }),
        signal: controller.signal,
      });
      if (!res.ok) return null;
      const data = await res.json();
      const items = Array.isArray(data) ? data : (data.items || []);
      return {
        from: word && word.from !== word.to ? word.from : context.pos,
        options: items.map((it) => ({
          label: it.label,
          type: KIND_MAP[it.kind] || 'text',
          apply: it.insertText || undefined,
          detail: it.detail || undefined,
          info: it.documentation || undefined,
          boost: -(it.sortPriority || 0), // меньший SortPriority — выше в списке
        })),
        // Точка не входит в validFor: новый квалификатор — новый контекст, нужен перезапрос.
        validFor: /^[\w"$]*$/,
      };
    } catch (e) {
      return null; // AbortError и сетевые ошибки — просто без подсказок
    }
  }

  /// Локальные варианты, которых нет в серверном ответе. Серверные точнее:
  /// у них разобранный scope (CTE, подзапросы, FK-сниппеты).
  function merge(local, server) {
    if (!server) return local;
    if (!local) return server;
    const seen = new Set(server.options.map((o) => o.label));
    const extra = local.options.filter((o) => !seen.has(o.label));
    return extra.length ? { ...server, options: server.options.concat(extra) } : server;
  }

  function localResult(context) {
    return window.WebDbCompletion.localCompletions({
      text: context.state.doc.toString(),
      pos: context.pos,
      dsId: textarea.dataset.dsId,
      schema: currentSchema(),
      dialect: textarea.dataset.dialect,
    });
  }

  /// Запрашивает сервер в фоне и, когда ответ пришёл, перезапускает список:
  /// на втором проходе источник увидит serverAnswer и объединит его с локальным.
  /// Debounce обязателен: локальный результат отдаётся без validFor, поэтому источник
  /// вызывается на каждую букву — без задержки это был бы запрос на каждое нажатие.
  function requestServer(context, word, view, immediate) {
    const key = answerKey(context);
    if (timer) { clearTimeout(timer); timer = null; }
    const run = () => {
      timer = null;
      fetchCompletions(context, word).then((result) => {
        if (!result) return;
        serverAnswer = { key, result };
        if (view) startCompletion(view);
      });
    };
    if (immediate) run();
    else timer = setTimeout(run, COMPLETION_DEBOUNCE_MS);
  }

  return (context) => {
    const chain = context.matchBefore(/[\w"$.]*/);
    const word = context.matchBefore(/[\w"$]*/);
    if (!context.explicit && (!chain || chain.from === chain.to)) return null;
    if (!textarea.dataset.dsId) return null;
    // Кэш метаданных строится только для базы из настроек подключения: в чужой базе
    // подсказки объектов были бы из другой БД — не предлагаем их вовсе.
    if (!isPrimaryDatabaseSelected()) return null;

    const key = answerKey(context);
    const local = localResult(context);

    // Фаза 2: ответ сервера для этой самой каретки уже получен — отдаём объединённый список.
    if (serverAnswer && serverAnswer.key === key) {
      const merged = merge(local, serverAnswer.result);
      serverAnswer = null;
      return merged;
    }

    // Фаза 1: локальные варианты сразу, сервер — в фоне (с debounce, кроме Ctrl+Space).
    if (local) {
      if (pendingResolve) { pendingResolve(null); pendingResolve = null; }
      requestServer(context, word, context.view, context.explicit);
      return local;
    }

    // Снапшота нет (не загрузился, схема слишком большая) — прежнее поведение.
    if (context.explicit) {
      dropPending();
      return fetchCompletions(context, word);
    }

    return new Promise((resolve) => {
      dropPending();
      pendingResolve = resolve;
      timer = setTimeout(async () => {
        timer = null;
        pendingResolve = null;
        resolve(await fetchCompletions(context, word));
      }, COMPLETION_DEBOUNCE_MS);
    });
  };
}
```

- [ ] **Step 3: Импортировать `startCompletion`**

В строке импорта из `@codemirror/autocomplete` добавить `startCompletion`:

```js
import { autocompletion, startCompletion } from '@codemirror/autocomplete';
```

- [ ] **Step 4: Собрать бандл и убедиться, что сборка проходит**

```bash
npm --prefix frontend run build
```

Ожидается: esbuild печатает список выходных файлов без ошибок.

**Важно:** результат сборки (`src/WebDbViewer.Web/wwwroot/js/*.js`) **не коммитить** — бандл собирает основная сессия после батча. Сборка здесь нужна только чтобы поймать синтаксические ошибки.

- [ ] **Step 5: Коммит**

```bash
git add frontend/src/editor.js
git commit -m "feat: two-phase completion source with local snapshot"
```

---

### Task 4: Инвалидация метаданных и кнопка «Обновить метаданные»

**Files:**
- Create: `src/WebDbViewer.Web/Api/MetadataRefreshEndpoints.cs`
- Modify: `src/WebDbViewer.Web/Program.cs` — добавить `app.MapMetadataRefreshApi();`
- Modify: `src/WebDbViewer.Web/Pages/Editor/_EditorScope.cshtml` — кнопка после селекта схемы
- Modify: `frontend/src/app.js` — обработчик клика
- Test: `tests/WebDbViewer.Tests.Unit/Completion/MetadataRefreshTests.cs`

**Interfaces:**
- Consumes: `IMetadataCache.InvalidateAsync(Guid, string?, CancellationToken)`, `IMetadataCache.WarmupAsync(Guid, IReadOnlyList<string>, CancellationToken)`, `IDataSourceStore.GetAsync`, `CompletionEndpoints.DefaultSchemaFor(DataSourceConfig, string?)` (публичный статический метод, уже существует).
- Produces: `POST /api/metadata/refresh` с телом `{dsId, schema}` → `202`; `IEndpointRouteBuilder.MapMetadataRefreshApi()`; кнопка `[data-action="refresh-metadata"]` в тулбаре.
- **Не трогает** `frontend/src/editor.js` и `src/WebDbViewer.Web/Api/CompletionEndpoints.cs` — они принадлежат другим задачам этого батча.

- [ ] **Step 1: Написать падающий тест на инвалидацию**

Создать `tests/WebDbViewer.Tests.Unit/Completion/MetadataRefreshTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using WebDbViewer.Core;
using WebDbViewer.Metadata;

namespace WebDbViewer.Tests.Unit.Completion;

public class MetadataRefreshTests
{
    /// <summary>Загрузчик со счётчиком: показывает, ходил ли кэш в базу заново.</summary>
    private sealed class CountingLoader : IMetadataLoader
    {
        public int Calls;

        public Task<SchemaSnapshot> LoadAsync(Guid dataSourceId, string schemaName, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SchemaSnapshot
            {
                SchemaName = schemaName,
                Tables = [],
                LoadedAt = DateTimeOffset.UtcNow,
                VersionHash = "v" + Calls,
            });
        }
    }

    private sealed class NullSnapshotStore : ISnapshotStore
    {
        public Task SaveAsync(Guid dataSourceId, SchemaSnapshot snapshot, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid dataSourceId, string? schemaName, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PersistedSnapshot>> LoadAllAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersistedSnapshot>>([]);
    }

    [Fact]
    public async Task Invalidate_ForcesReload()
    {
        var loader = new CountingLoader();
        var cache = new MetadataCache(loader, new NullSnapshotStore(), Options.Create(new MetadataCacheOptions()));
        var dsId = Guid.NewGuid();

        await cache.GetSchemaAsync(dsId, "public", CancellationToken.None);
        await cache.GetSchemaAsync(dsId, "public", CancellationToken.None);
        Assert.Equal(1, loader.Calls); // второй запрос обслужен из кэша

        await cache.InvalidateAsync(dsId, "public", CancellationToken.None);
        await cache.GetSchemaAsync(dsId, "public", CancellationToken.None);

        Assert.Equal(2, loader.Calls);
    }
}
```

**Перед запуском** сверить сигнатуры `IMetadataLoader`, `ISnapshotStore`, `PersistedSnapshot` и конструктора `MetadataCache` с исходниками в `src/WebDbViewer.Metadata/` и поправить фейки, если они разошлись — интерфейсы там могли поменяться.

- [ ] **Step 2: Запустить тест — убедиться, что он проходит**

```bash
dotnet test tests/WebDbViewer.Tests.Unit/WebDbViewer.Tests.Unit.csproj -o "$env:TEMP\wdb-task4" --filter FullyQualifiedName~MetadataRefreshTests
```

Ожидается: PASS. Этот тест закрепляет поведение, на которое опирается endpoint; если он падает — дефект в `MetadataCache`, разбираться с ним, а не подгонять тест.

- [ ] **Step 3: Создать `src/WebDbViewer.Web/Api/MetadataRefreshEndpoints.cs`**

```csharp
using WebDbViewer.Core;

namespace WebDbViewer.Web.Api;

/// <summary>Тело запроса ручного обновления метаданных.</summary>
/// <param name="DsId">Идентификатор датасорса.</param>
/// <param name="Schema">Схема; null — схема по умолчанию для датасорса.</param>
public sealed record MetadataRefreshRequest(Guid DsId, string? Schema = null);

/// <summary>
/// Ручное обновление кэша метаданных. Нужно после DDL: без него подсказки
/// живут по TTL и новых объектов не видят.
/// </summary>
public static class MetadataRefreshEndpoints
{
    public static IEndpointRouteBuilder MapMetadataRefreshApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/metadata/refresh", RefreshAsync).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Сбрасывает снапшот схемы и запускает прогрев в фоне. Возвращает 202 сразу:
    /// интроспекция большой схемы длится секунды, держать ради неё ответ незачем.
    /// </summary>
    private static async Task<IResult> RefreshAsync(
        MetadataRefreshRequest request,
        IDataSourceStore dataSourceStore,
        IMetadataCache metadata,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (request.DsId == Guid.Empty)
            return Results.BadRequest(new { error = "Не задан датасорс." });

        var config = await dataSourceStore.GetAsync(request.DsId, ct);
        if (config is null)
            return Results.NotFound(new { error = "Датасорс не найден." });

        var schema = CompletionEndpoints.DefaultSchemaFor(config, request.Schema);
        if (string.IsNullOrWhiteSpace(schema))
            return Results.Accepted();

        await metadata.InvalidateAsync(request.DsId, schema, ct);

        var logger = loggerFactory.CreateLogger(typeof(MetadataRefreshEndpoints));
        // Токен запроса не передаётся: прогрев переживает завершение HTTP-ответа.
        _ = Task.Run(async () =>
        {
            try
            {
                await metadata.WarmupAsync(request.DsId, [schema], CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Обновление метаданных {Schema} датасорса {DataSourceId} не удалось",
                    schema, request.DsId);
            }
        }, CancellationToken.None);

        return Results.Accepted();
    }
}
```

- [ ] **Step 4: Зарегистрировать endpoint в `Program.cs`**

Найти строку `app.MapCompletionApi();` и добавить сразу после неё:

```csharp
    app.MapMetadataRefreshApi();
```

- [ ] **Step 5: Добавить кнопку в тулбар**

В `src/WebDbViewer.Web/Pages/Editor/_EditorScope.cshtml` после закрывающего `</select>` селекта схемы добавить:

```html
<button type="button" class="icon-btn" data-action="refresh-metadata"
        aria-label="Обновить метаданные схемы"
        data-tip="Перечитать список таблиц и колонок схемы — после изменения структуры БД">@UiIcons.Refresh</button>
```

Сверить с `src/WebDbViewer.Web/Pages/Data.cshtml:55`, как оформлены соседние иконочные кнопки (классы и атрибуты), и повторить принятый там стиль. Если в `_EditorScope.cshtml` нет `@using` для `UiIcons`, добавить его так же, как в `Data.cshtml`.

- [ ] **Step 6: Добавить обработчик клика в `frontend/src/app.js`**

В конец файла:

```js
// Обновление метаданных схемы: сбрасываем серверный кэш и клиентский снапшот,
// затем грузим его заново. Делегирование на document — кнопка живёт в HTMX-фрагменте
// тулбара и пересоздаётся при каждой смене датасорса или базы.
document.addEventListener('click', async (e) => {
  const btn = e.target.closest ? e.target.closest('[data-action="refresh-metadata"]') : null;
  if (!btn) return;
  e.preventDefault();

  const ds = document.querySelector('[data-role="datasource-select"]');
  const schemaSelect = document.querySelector('[data-role="schema-select"]');
  const dsId = ds && ds.value;
  const schema = schemaSelect && schemaSelect.value ? schemaSelect.value : null;
  if (!dsId) return;

  btn.setAttribute('aria-disabled', 'true');
  try {
    await fetch('/api/metadata/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ dsId, schema }),
    });
    if (window.WebDbCompletion) {
      window.WebDbCompletion.reset(dsId, schema);
      await window.WebDbCompletion.load(dsId, schema);
    }
    if (window.WebDb && typeof window.WebDb.toast === 'function') {
      window.WebDb.toast('Метаданные схемы обновляются', 'info');
    }
  } catch (_) {
    if (window.WebDb && typeof window.WebDb.toast === 'function') {
      window.WebDb.toast('Не удалось обновить метаданные', 'error');
    }
  } finally {
    btn.removeAttribute('aria-disabled');
  }
});
```

Перед вставкой проверить в `frontend/src/app.js`, как называется тост-функция и какие уровни она принимает (`window.WebDb.toast(message, level)` используется в `editor.js`); при расхождении — использовать фактическую сигнатуру.

- [ ] **Step 7: Собрать и прогнать тесты**

```bash
dotnet build WebDbViewerSol.slnx -o "$env:TEMP\wdb-task4"
```

Ожидается: Build succeeded, 0 errors.

```bash
npm --prefix frontend run build
```

Ожидается: сборка без ошибок. Результат сборки **не коммитить**.

```bash
dotnet test tests/WebDbViewer.Tests.Unit/WebDbViewer.Tests.Unit.csproj -o "$env:TEMP\wdb-task4"
```

Ожидается: все тесты PASS.

- [ ] **Step 8: Коммит**

```bash
git add src/WebDbViewer.Web/Api/MetadataRefreshEndpoints.cs src/WebDbViewer.Web/Program.cs src/WebDbViewer.Web/Pages/Editor/_EditorScope.cshtml frontend/src/app.js tests/WebDbViewer.Tests.Unit/Completion/MetadataRefreshTests.cs
git commit -m "feat: manual metadata refresh endpoint and toolbar button"
```

---

### Task 5: `RESULT_CACHE` в словарных запросах Oracle и замеры интроспекции

**Files:**
- Modify: `src/WebDbViewer.Providers.Oracle/OracleProvider.cs` — пять словарных запросов интроспекции схемы
- Modify: `src/WebDbViewer.Metadata/MetadataCache.cs` — метод `LoadCoreAsync`

**Interfaces:**
- Consumes: ничего из других задач батча.
- Produces: лог-сообщение уровня Debug с шаблоном `"Интроспекция схемы {Schema} датасорса {DataSourceId}: {ElapsedMs} мс, таблиц {Tables}"` — Task 7 ищет в логах именно его.

- [ ] **Step 1: Добавить хинт `RESULT_CACHE` в словарные запросы**

В `src/WebDbViewer.Providers.Oracle/OracleProvider.cs`, в методе интроспекции схемы, добавить `/*+ RESULT_CACHE */` сразу после `SELECT` в пяти запросах (искать по комментариям `// 1. Объекты`, `// 2. Комментарии таблиц`, `// 3. Комментарии колонок`, `// 4. Первичные ключи`, `// 5. Колонки`):

```csharp
                SELECT /*+ RESULT_CACHE */ o.object_name, o.object_type
                FROM all_objects o
```

```csharp
                SELECT /*+ RESULT_CACHE */ c.table_name, c.comments
                FROM all_tab_comments c
```

```csharp
                SELECT /*+ RESULT_CACHE */ c.table_name, c.column_name, c.comments
                FROM all_col_comments c
```

```csharp
                SELECT /*+ RESULT_CACHE */ c.table_name, cc.column_name
                FROM all_constraints c
```

```csharp
                SELECT /*+ RESULT_CACHE */ c.table_name, c.column_name, c.data_type, c.data_length, c.data_precision, c.data_scale,
                       c.nullable, c.data_default, c.column_id
                FROM all_tab_columns c
```

Больше в файле ничего не менять: запросы к `v$version`, `v$instance`, `all_users`, `all_db_links` и одиночные запросы колонок таблицы хинт не получают — они либо разовые, либо возвращают меняющиеся данные.

Над первым изменённым запросом добавить комментарий (английский, как остальной код в файле):

```csharp
        // RESULT_CACHE: schema introspection repeats the same dictionary queries per editor session.
        // Oracle catalogs are slow, and the result set changes only on DDL.
```

- [ ] **Step 2: Добавить замер длительности интроспекции**

В `src/WebDbViewer.Metadata/MetadataCache.cs`, метод `LoadCoreAsync`, обернуть вызов загрузчика:

```csharp
    private async Task<SchemaSnapshot> LoadCoreAsync(Guid dataSourceId, string schemaName, SchemaSnapshot? previous, CancellationToken ct)
    {
        var startedAt = _time.GetTimestamp();
        var snapshot = await _loader.LoadAsync(dataSourceId, schemaName, ct).ConfigureAwait(false);
        _logger.LogDebug("Интроспекция схемы {Schema} датасорса {DataSourceId}: {ElapsedMs} мс, таблиц {Tables}",
            schemaName, dataSourceId,
            (int)_time.GetElapsedTime(startedAt).TotalMilliseconds, snapshot.Tables.Count);

        if (snapshot.LoadedAt == default)
```

Остальная часть метода не меняется. `_time` — существующее поле `TimeProvider`; `GetTimestamp()` и `GetElapsedTime(long)` — его штатные методы, новых полей заводить не нужно.

- [ ] **Step 3: Собрать и прогнать тесты**

```bash
dotnet build WebDbViewerSol.slnx -o "$env:TEMP\wdb-task5"
```

Ожидается: Build succeeded, 0 errors.

```bash
dotnet test tests/WebDbViewer.Tests.Unit/WebDbViewer.Tests.Unit.csproj -o "$env:TEMP\wdb-task5"
```

Ожидается: все тесты PASS (в частности `SingleFlightTests`, который использует `MetadataCache` с фейковым `TimeProvider` — если он падает, значит фейк не поддерживает `GetTimestamp`; в этом случае использовать `TimeProvider.System.GetTimestamp()`-независимый замер через `System.Diagnostics.Stopwatch.GetTimestamp()` и `Stopwatch.GetElapsedTime`).

- [ ] **Step 4: Коммит**

```bash
git add src/WebDbViewer.Providers.Oracle/OracleProvider.cs src/WebDbViewer.Metadata/MetadataCache.cs
git commit -m "perf: RESULT_CACHE hints for Oracle dictionary queries, log introspection time"
```

---

## Барьер между батчами (выполняет основная сессия, не агент)

- [ ] Дождаться завершения всех пяти задач батча 1.
- [ ] Собрать фронтенд-бандл один раз: `npm --prefix frontend run build`
- [ ] Проверить сборку и тесты целиком: `dotnet build WebDbViewerSol.slnx` и `dotnet test tests/WebDbViewer.Tests.Unit/WebDbViewer.Tests.Unit.csproj`
- [ ] Закоммитить бандл: `git add src/WebDbViewer.Web/wwwroot/js/editor.js src/WebDbViewer.Web/wwwroot/js/app.js && git commit -m "chore: rebuild frontend bundle"`

---

# Батч 2 — проверка и приёмка (задачи 6–10, запускаются параллельно)

---

### Task 6: Интеграционный тест снапшота и инвалидации на живом PostgreSQL

**Files:**
- Create: `tests/WebDbViewer.Tests.Integration/Completion/SchemaMapIntegrationTests.cs`

**Interfaces:**
- Consumes: `SchemaMapDto.From(SchemaSnapshot)` (Task 1), `MetadataCache.GetSchemaAsync / InvalidateAsync`, `PostgresProvider`.
- Produces: тест-класс `SchemaMapIntegrationTests`.

HTTP-хост в этих тестах не поднимается: существующие интеграционные тесты работают с провайдером напрямую, и новых пакетов (`Microsoft.AspNetCore.Mvc.Testing`) в air-gapped проект тянуть нельзя. Проверяется связка «провайдер → кэш → DTO», HTTP-обвязку покрывает браузерная проверка (Task 8).

- [ ] **Step 1: Повторить механику существующих тестов**

Прочитать `tests/WebDbViewer.Tests.Integration/Tree/PostgresTreeIntrospectionTests.cs`. Взять оттуда: строку подключения по умолчанию `Host=localhost;Port=5432;Database=webdbviewer_demo;Username=postgres;Password=1;Pooling=true;`, переменную окружения `WEBDBVIEWER_TEST_DEMO_DB`, схему `demo_core`, паттерн `IAsyncLifetime` с флагом `available` и пропуском теста при недоступной базе. Свой механизм не изобретать.

Прочитать `src/WebDbViewer.Web/Services/DbMetadataLoader.cs` и собрать `IMetadataLoader` тем же способом. Если он требует инфраструктуры Web-слоя (сессии, стор датасорсов), написать в тестовом файле минимальный `IMetadataLoader`, который зовёт `PostgresProvider` напрямую на открытом соединении, — так же, как это делают соседние тесты.

- [ ] **Step 2: Написать тест «новая колонка видна только после инвалидации»**

Сценарий:

1. `CREATE TABLE demo_core.wdb_schema_map_probe (id bigint primary key, note text)`.
2. `cache.GetSchemaAsync(dsId, "demo_core", ct)` → `SchemaMapDto.From(...)`: таблица `wdb_schema_map_probe` присутствует, у колонки `id` — `IsPrimaryKey == true`, `Partial == false`.
3. Запомнить `SchemaMapDto.ETagFor(snapshot)` — он не `null` (Postgres-провайдер отдаёт `VersionHash`). Если `null` — это находка, зафиксировать: клиентское кэширование по ETag на PostgreSQL работать не будет.
4. `ALTER TABLE demo_core.wdb_schema_map_probe ADD COLUMN extra text`.
5. Повторный `GetSchemaAsync` — колонки `extra` нет (снапшот отдан из кэша).
6. `await cache.InvalidateAsync(dsId, "demo_core", ct)`, затем `GetSchemaAsync` — колонка `extra` появилась, а `ETagFor` вернул другое значение.
7. В `finally` — `DROP TABLE IF EXISTS demo_core.wdb_schema_map_probe`.

- [ ] **Step 3: Запустить тест**

```bash
dotnet test tests/WebDbViewer.Tests.Integration/WebDbViewer.Tests.Integration.csproj -o "$env:TEMP\wdb-task6" --filter FullyQualifiedName~SchemaMapIntegrationTests
```

Ожидается: PASS на живой базе. Если тест падает — это находка, а не повод его ослабить: зафиксировать, что именно разошлось (шаг сценария, ожидание, факт).

- [ ] **Step 4: Коммит**

```bash
git add tests/WebDbViewer.Tests.Integration/SchemaMapIntegrationTests.cs
git commit -m "test: integration coverage for schema-map and metadata refresh"
```

---

### Task 7: Замеры интроспекции Oracle до и после `RESULT_CACHE`

**Files:**
- Create: `docs/superpowers/reports/2026-08-10-oracle-introspection-timing.md`

**Interfaces:**
- Consumes: лог-сообщение `"Интроспекция схемы {Schema} датасорса {DataSourceId}: {ElapsedMs} мс, таблиц {Tables}"` (Task 5).
- Produces: отчёт с числами и вердиктом «хинты оставить / убрать».

- [ ] **Step 1: Снять базовые замеры (до хинтов)**

Собрать приложение из коммита, предшествующего коммиту Task 5 (`git log --oneline -- src/WebDbViewer.Providers.Oracle/OracleProvider.cs`), в отдельный каталог, поднять на своём порту:

```bash
dotnet run --project src/WebDbViewer.Web/DbViewer.App.csproj --urls http://localhost:5107
```

Подключиться к живой базе Oracle, открыть редактор, снять из логов время интроспекции. Повторить 5 раз с холодного старта приложения (кэш метаданных живёт в процессе — без перезапуска второй замер бессмысленен).

- [ ] **Step 2: Снять замеры с хинтами**

То же самое на текущем `main`. 5 запусков.

- [ ] **Step 3: Записать отчёт**

Файл `docs/superpowers/reports/2026-08-10-oracle-introspection-timing.md`: таблица «запуск / до, мс / после, мс», медиана и p95 по каждой серии, число таблиц в схеме, версия Oracle (`SELECT banner FROM v$version`), вердикт.

**Критерий:** если медиана не улучшилась хотя бы на 10%, рекомендовать убрать хинты — код без них проще, а `RESULT_CACHE` занимает место в общем пуле результатов сервера.

- [ ] **Step 4: Коммит**

```bash
git add docs/superpowers/reports/2026-08-10-oracle-introspection-timing.md
git commit -m "docs: Oracle introspection timing before and after RESULT_CACHE"
```

---

### Task 8: Проверка редактора в браузере

**Files:**
- Create: `docs/superpowers/reports/2026-08-10-completion-browser-verification.md`

**Interfaces:**
- Consumes: собранное приложение целиком (батч 1 + барьер).
- Produces: отчёт с результатом по каждому критерию приёмки спеки.

- [ ] **Step 1: Поднять приложение**

```bash
dotnet run --project src/WebDbViewer.Web/DbViewer.App.csproj --urls http://localhost:5108
```

Проверять **рендером страницы** (browser-инструменты), а не поиском подстрок в HTML: класс дефектов «200 OK с правильным текстом в теле и полностью сломанным поведением» именно так и проходит мимо.

- [ ] **Step 2: Проверить, что попап открывается без сети**

Открыть редактор на датасорсе PostgreSQL, дождаться загрузки снапшота (в Network — один `schema-map`). Очистить Network, набрать `sel` → `SELECT * FROM u`. Ожидается: список таблиц появляется **до** появления запроса `/api/completion` в Network.

Зафиксировать: появился ли попап мгновенно, сколько запросов ушло.

Отдельно — быстрый набор 10 символов подряд: в Network должен появиться **один** запрос `/api/completion`, а не десять. Десять означает, что debounce серверного запроса потерян.

- [ ] **Step 3: Замерить p95 локальной фильтрации**

В консоли браузера после 30–50 нажатий:

```js
window.WebDbCompletion.stats()
```

Ожидается: `p95 < 50`. Записать фактические `count`, `p50`, `p95`.

- [ ] **Step 4: Проверить паритет вставки алиаса**

Набрать `SELECT * FROM ` и выбрать таблицу с составным именем (например `order_items`) **сразу**, до прихода серверного ответа. Записать вставленный текст. Повторить, подождав секунду перед выбором (серверный ответ уже пришёл). Ожидается: одинаковый текст, вида `order_items oi`.

- [ ] **Step 5: Проверить отсутствие дублей и работу CTE**

Набрать:

```sql
WITH recent AS (SELECT * FROM orders)
SELECT r. FROM recent r
```

Поставить каретку после `r.`. Ожидается: колонки `orders`, каждая ровно один раз.

- [ ] **Step 6: Проверить 304 при повторном открытии**

Перезагрузить страницу редактора, в Network найти второй `schema-map`. Ожидается: статус `304`, размер тела 0.

- [ ] **Step 7: Проверить кнопку «Обновить метаданные»**

В живой базе создать таблицу `wdb_probe`. В редакторе набрать `SELECT * FROM wdb_` — таблицы быть не должно. Нажать кнопку обновления метаданных, подождать, повторить — таблица должна появиться. Затем `DROP TABLE wdb_probe`.

- [ ] **Step 8: Проверить обе темы и Oracle-датасорс**

Переключить тему (тёмная/светлая) — попап читаем в обеих. Переключить датасорс на Oracle — подсказки приходят, имена в верхнем регистре, алиас в нижнем.

- [ ] **Step 9: Записать отчёт и закоммитить**

Файл `docs/superpowers/reports/2026-08-10-completion-browser-verification.md`: таблица «критерий / ожидание / факт / вывод» по шагам 2–8, со скриншотами при расхождениях.

```bash
git add docs/superpowers/reports/2026-08-10-completion-browser-verification.md
git commit -m "docs: browser verification report for completion client cache"
```

---

### Task 9: Ревью диффа батча 1

**Files:**
- Create: `docs/superpowers/reports/2026-08-10-completion-code-review.md`

**Interfaces:**
- Consumes: дифф коммитов батча 1.
- Produces: отчёт с находками, ранжированными по серьёзности.

- [ ] **Step 1: Собрать дифф**

```bash
git log --oneline -8
```

Определить диапазон коммитов батча 1 и получить полный дифф с расширенным контекстом:

```bash
git diff -U20 <коммит-до-батча>..HEAD -- src frontend tests
```

**Дифф с ограниченным контекстом обрывается после последней правки — конец хунка не равен концу файла.** Прежде чем писать находку «здесь не хватает проверки», открыть файл целиком и убедиться, что проверки действительно нет.

- [ ] **Step 2: Проверить конкретные риски**

Не общий проход, а именно эти:

1. **Утечка данных между датасорсами.** Ключ клиентского кэша — `dsId + '/' + schema`. Проверить, что смена датасорса или базы не отдаёт подсказки из чужой схемы; что `isPrimaryDatabaseSelected()` по-прежнему отсекает неосновную базу.
2. **Гонка фаз.** `serverAnswer` сравнивается по ключу «позиция|текст». Проверить, что устаревший ответ не может быть показан к другому запросу и что `serverAnswer` не залипает, блокируя следующий цикл.
3. **Инъекции.** Имя схемы приходит из тулбара в `schema-map` и `refresh`. Проверить, что оно нигде не склеивается в SQL напрямую, а идёт параметром.
4. **Авторизация.** Оба новых endpoint'а под `RequireAuthorization()`; `refresh` не даёт инвалидировать чужой датасорс сверх того, что уже позволяет `IDataSourceStore`.
5. **Расхождение правил.** JS-`makeAlias` против `SemanticCompleter.MakeAlias`, JS-`quote` против `SqlIdentifierQuoting.Quote` — сверить символ в символ, включая обработку цифр, `$`, `#` и Unicode-букв.
6. **Порог `partial`.** При `partial: true` клиент не должен предлагать колонки как будто они известны.
7. **Частота запросов к серверу.** Локальный результат отдаётся без `validFor`, поэтому источник вызывается на каждую букву. Убедиться, что серверный запрос при этом всё равно проходит через debounce и что предыдущий `fetch` отменяется, — иначе вместо экономии получится запрос на каждое нажатие. Проверяется вкладкой Network: за быстрый набор 10 символов должен уйти один запрос `/api/completion`, не десять.
8. **Мёртвый код.** После удаления warmup не осталось ли неиспользуемых полей, констант и импортов (`COMPLETION_DEBOUNCE_MS` всё ещё нужен — он в fallback-ветке и в debounce серверного запроса).

- [ ] **Step 3: Проверить спорные находки самому**

Каждую находку уровня Critical/High подтвердить фактом: строкой файла, воспроизведением или тестом. Находка без подтверждения идёт в отчёт как «требует проверки», а не как дефект.

- [ ] **Step 4: Записать отчёт и закоммитить**

Файл `docs/superpowers/reports/2026-08-10-completion-code-review.md`: находки по убыванию серьёзности, для каждой — файл:строка, в чём дефект, как воспроизвести, предлагаемая правка. Правки **не применять** — решение принимает основная сессия.

```bash
git add docs/superpowers/reports/2026-08-10-completion-code-review.md
git commit -m "docs: code review report for completion client cache"
```

---

### Task 10: Актуализировать документацию

**Files:**
- Modify: `docs/SQL Code Completion.md`

**Interfaces:**
- Consumes: спека `docs/superpowers/specs/2026-08-10-sql-code-completion-design.md`, коммиты батча 1.
- Produces: раздел «Что реализовано» в исследовании.

- [ ] **Step 1: Добавить раздел в конец `docs/SQL Code Completion.md`**

Раздел `## Что реализовано (2026-08-10)` со следующим содержанием, выверенным по фактическому коду:

- какие пункты исследования были уже закрыты до этой итерации (серверный ANTLR-движок, кэш метаданных, `ALL_*` с фильтром по `OWNER`, debounce, автоалиасы, FK-сниппеты, signature help, frecency);
- что добавлено этой итерацией (клиентский снапшот `schema-map` с ETag, двухфазный источник, кнопка обновления метаданных, `RESULT_CACHE`, замеры);
- что сознательно не делалось и почему (список из раздела 4 спеки);
- ссылки на спеку, план и отчёты батча 2.

Никаких прогнозов и оценок — только состояние кода. Раздел «Диагностика текущего состояния» выше по файлу помечен как неверифицированный; добавить в его начало одну строку: `> Диагностика ниже написана до чтения кода. Фактическое состояние — в разделе «Что реализовано» в конце файла.`

- [ ] **Step 2: Проверить ссылки**

Убедиться, что все упомянутые файлы существуют:

```bash
ls docs/superpowers/specs/2026-08-10-sql-code-completion-design.md docs/superpowers/plans/2026-08-10-sql-completion-client-cache.md docs/superpowers/reports/
```

- [ ] **Step 3: Коммит**

```bash
git add "docs/SQL Code Completion.md"
git commit -m "docs: record implemented state of SQL code completion"
```

---

## Итог батчей

| Батч | Агентов | Что проверяется на выходе |
|---|---|---|
| 1 | 5 (задачи 1–5) | `dotnet build` и `dotnet test` зелёные, фронтенд собирается |
| Барьер | основная сессия | бандл собран и закоммичен один раз |
| 2 | 5 (задачи 6–10) | интеграционный тест, замеры Oracle, браузерная проверка, ревью, документация |

После батча 2 основная сессия разбирает отчёты задач 7–9 и решает, что чинить, — исправления в этот план не входят.
