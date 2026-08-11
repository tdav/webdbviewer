# Контекстный фильтр мгновенной фазы автодополнения — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** После `FROM` — только таблицы, после `SELECT`/`WHERE`/`SET` — колонки и функции, словарь диалекта — лишь там, где он уместен, и всегда ниже объектов.

**Architecture:** `localCompletions` в `completion-schema.js` классифицирует контекст курсора в одну из четырёх категорий и возвращает её в результате; `localResult` в `editor.js` добавляет `keywordCompletionSource` только для категории `general`, с `boost` ниже объектов. Сервер (ANTLR c3) не меняется — его ответ уточняет список через ~250 мс.

**Tech Stack:** CodeMirror 6 (`@codemirror/autocomplete`, `@codemirror/lang-sql`), esbuild.

**Спека:** `docs/superpowers/specs/2026-08-11-completion-context-filter-design.md`

## Global Constraints

- Комментарии в коде — русский (конвенция проекта); commit-сообщения — английский.
- Никаких новых зависимостей (air-gapped). `git add` только точными путями.
- Бандл `src/WebDbViewer.Web/wwwroot/js/*.js` пересобирается (`npm --prefix frontend run build`) и **включается в коммит**.
- JS-тест-раннера нет — проверка браузерная; C#-код не меняется, unit-сюита должна остаться зелёной без правок.
- Контракт `window.WebDbCompletion` расширяется совместимо: у результата `localCompletions` появляется поле `context`, существующие поля не меняются.

---

### Task 1: Классификатор контекста и поле `context`

**Files:**
- Modify: `frontend/src/completion-schema.js:109-164`

**Interfaces:**
- Produces: результат `localCompletions` — `{ from, options, context } | null`, где `context ∈ 'qualifier' | 'table' | 'column' | 'general'`. `null` — только при отсутствии снапшота. Пустой `options` при распознанном контексте — валидный результат (Task 2 по нему решает судьбу ключевых слов).

- [ ] **Step 1: Добавить набор слов колоночного контекста**

После строки `const TABLE_CONTEXT = ...` (строка 109):

```js
// Позиция значения/колонки: после этих слов уместны колонки и функции,
// но не словарь диалекта. BY покрывает GROUP BY и ORDER BY.
const COLUMN_CONTEXT = new Set([
  'select', 'distinct', 'set', 'where', 'on', 'having', 'by',
  'and', 'or', 'when', 'then', 'else', 'not', 'in',
]);
```

- [ ] **Step 2: Классифицировать контекст и вернуть его в результате**

В `localCompletions` заменить цепочку `if (qualifier) ... else if ... else ...` (строки 136–159) и `return` (строка 163) на:

```js
  // Категория контекста уходит в editor.js: по ней он решает, добавлять ли
  // ключевые слова диалекта. Точная грамматика — забота серверного c3;
  // здесь достаточно грубой классификации по предыдущему слову.
  let context = 'general';
  if (qualifier) {
    context = 'qualifier';
    // «alias.» или «table.» — колонки соответствующей таблицы и ничего больше.
    const q = qualifier[1].replace(/^"|"$/g, '').toLowerCase();
    const ref = refs.find((r) => (r.alias || '').toLowerCase() === q)
      || refs.find((r) => r.name.toLowerCase() === q);
    const table = tableByName.get((ref ? ref.name : q).toLowerCase());
    if (table) pushColumns(options, table, PRIORITY.scopeColumn, dialect);
  } else if (prevWord && TABLE_CONTEXT.has(prevWord.toLowerCase())) {
    context = 'table';
    // Позиция имени таблицы: только таблицы и вью.
    // ponytail: автоалиас (SemanticCompleter.MakeAlias) здесь раньше добавлялся
    // безусловно, а сервер — только при включённом CompletionOptions.AutoAliasTables
    // (по умолчанию false, /api/completion его не включает). Расхождение — прямой
    // баг: один и тот же вариант вставлял разный текст до/после ответа сервера.
    // Пока сервер не включит опцию, клиент алиас не добавляет.
    pushTables(options, snapshot.tables, dialect);
  } else if (prevWord && COLUMN_CONTEXT.has(prevWord.toLowerCase())) {
    context = 'column';
    // Позиция колонки/значения: колонки таблиц statement'а и функции схемы.
    for (const ref of refs) {
      const table = tableByName.get(ref.name.toLowerCase());
      if (table) pushColumns(options, table, PRIORITY.scopeColumn, dialect);
    }
    pushRoutines(options, snapshot.routines, dialect);
  } else {
    // Общий случай: колонки таблиц текущего statement, затем таблицы и функции.
    for (const ref of refs) {
      const table = tableByName.get(ref.name.toLowerCase());
      if (table) pushColumns(options, table, PRIORITY.scopeColumn, dialect);
    }
    pushTables(options, snapshot.tables, dialect);
    pushRoutines(options, snapshot.routines, dialect);
  }

  timings.push(performance.now() - started);
  if (timings.length > 500) timings.shift();
  // Пустой options при распознанном контексте — валидный результат: editor.js
  // по context решает, добавлять ли ключевые слова, даже когда объектов нет.
  return { from, options, context };
```

Обрати внимание: прежний `return options.length ? { from, options } : null;` удаляется — `null` остаётся только для случая «снапшота нет» (строка 115).

- [ ] **Step 3: Проверить синтаксис**

```bash
node --input-type=module --eval "await import('./frontend/src/completion-schema.js').catch(e => { if (!String(e).includes('window is not defined')) { console.error(e); process.exit(1); } })"
```

Ожидается: код выхода 0.

---

### Task 2: Ключевые слова только в `general`, ниже объектов

**Files:**
- Modify: `frontend/src/editor.js:176-194` (функция `localResult`)

**Interfaces:**
- Consumes: `localCompletions(...) → { from, options, context } | null` (Task 1); `keywordSourceFor(dialect)(context)` — уже существует в `editor.js`.

- [ ] **Step 1: Переписать `localResult`**

Заменить функцию целиком:

```js
  // Boost ключевых слов: ниже функций (−13) и на уровне серверных builtin (~−19),
  // чтобы объекты схемы всегда стояли выше словаря диалекта.
  const KEYWORD_BOOST = -19;

  function localResult(context) {
    const local = window.WebDbCompletion.localCompletions({
      text: context.state.doc.toString(),
      pos: context.pos,
      dsId: textarea.dataset.dsId,
      db: currentDatabase(),
      schema: currentSchema(),
      dialect: textarea.dataset.dialect,
    });
    // Ключевые слова — встроенным keywordCompletionSource (тот же диалект и регистр,
    // что и подсветка), свой список не заводим. Но только там, где словарь уместен:
    // после FROM/SELECT/WHERE и «x.» он лишь хоронит таблицы и колонки под собой
    // (категории table/column/qualifier). Точную грамматику даст сервер (c3) через
    // debounce; merge() дедуплицирует по label, когда его ответ придёт.
    if (local && local.context !== 'general') {
      return local.options.length ? local : null;
    }
    const keywords = keywordSourceFor(textarea.dataset.dialect)(context);
    const ranked = keywords
      ? { ...keywords, options: keywords.options.map((o) => ({ ...o, boost: KEYWORD_BOOST })) }
      : null;
    if (!ranked) return local && local.options.length ? local : null;
    if (!local) return ranked;
    // from берём из local: оба правила слова совпадают, а local считает его от каретки.
    const merged = local.options.concat(ranked.options);
    return { ...local, options: merged };
  }
```

Тонкость, которую нельзя потерять: при `context !== 'general'` и пустых `options` функция возвращает `null` — тогда внешний код уходит в fallback-ветку «снапшота нет» и спрашивает сервер немедленно/с debounce, как раньше. Пустой попап не показывается.

- [ ] **Step 2: Собрать бандл**

```bash
npm --prefix frontend run build
```

Ожидается: сборка без ошибок.

- [ ] **Step 3: Коммит (оба файла + бандл)**

```bash
git add frontend/src/completion-schema.js frontend/src/editor.js src/WebDbViewer.Web/wwwroot/js/editor.js
git commit -m "feat: context-aware keyword filtering in instant completion phase"
```

---

### Task 3: Браузерная проверка по критериям спеки

**Files:** нет (проверка + при находках — фиксы в файлы задач 1–2).

- [ ] **Step 1: Поднять приложение** (`.claude/launch.json`, конфигурация `webdbviewer`, порт 5199; вход admin/admin).

- [ ] **Step 2: Пройти таблицу критериев спеки** на живом датасорсе (Oracle «token» или PostgreSQL с выбранной базой):

| Ввод | Ожидание |
|---|---|
| `SELECT * FROM ` | только таблицы/вью; `ABORT`/`ALTER`/`AS` отсутствуют |
| `SELECT ` (при `FROM t` в тексте) | колонки `t` + функции |
| `WHERE ` | колонки + функции |
| `x.` | только колонки `x` |
| пустая строка + Ctrl+Space | ключевые слова (`SELECT`, `INSERT`, …) |
| `SELECT * FROM t ` | объекты выше, ключевые слова ниже |

- [ ] **Step 3: Регресс:** серверный ответ объединяется без дублей (подождать 250 мс, пересчитать список); в непервичной базе PostgreSQL ключевые слова живы (снапшот там есть теперь — проверить общий случай).

- [ ] **Step 4: Если всё зелёное — задача завершена** (коммит уже сделан в Task 2). Находки — чинить и повторять с Step 2.

## Итог

| Задача | Результат |
|---|---|
| 1 | `localCompletions` возвращает `context`, четыре категории |
| 2 | ключевые слова только в `general`, boost −19 |
| 3 | браузерная проверка критериев спеки |
