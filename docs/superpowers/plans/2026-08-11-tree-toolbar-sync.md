# Синхронизация дерева с тулбаром — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Клик по датасорсу/базе/схеме в дереве навигатора выставляет селекты тулбара и автоматически обновляет метаданные схемы.

**Architecture:** Узлы дерева получают `data-ds-id` и `data-tree-role` (Razor). Делегированный обработчик в `app.js` кладёт цель `{dsId, db, schema}` в очередь и применяет её пошагово через каскадные HTMX-свопы тулбара (`htmx:afterSwap` на `#editor-scope`); в конце зовёт `refreshMetadata` — единственную реализацию логики кнопки «Перечитать…», извлечённую из её обработчика.

**Tech Stack:** Razor Pages, HTMX, ванильный JS (esbuild).

**Спека:** `docs/superpowers/specs/2026-08-11-tree-toolbar-sync-design.md`

## Global Constraints

- Комментарии в коде — русский; commit-сообщения — английский.
- Никаких новых зависимостей. `git add` только точными путями.
- Бандл собрать (`npm --prefix frontend run build`) и включить `src/WebDbViewer.Web/wwwroot/js/app.js` в коммит.
- Синхронизацию запускают только узлы с `data-tree-role` (датасорс/база/схема); таблицы и папки — нет.
- Refresh — при каждом клике по такому узлу, в т.ч. повторном (решение владельца).
- Раскрытие узлов дерева не менять: обработчик не зовёт `preventDefault`/`stopPropagation`.
- Экземпляр владельца запущен из Visual Studio и держит `bin/` веб-проекта: **обычный `dotnet build`/`dotnet run` упадёт с MSB3027**. Сборка — только в изолированный каталог `-o "$env:TEMP\wdb-tree"`. Чужие процессы не убивать.

---

### Task 1: Атрибуты узлов дерева

**Files:**
- Modify: `src/WebDbViewer.Web/Pages/Shared/_TreeNode.cshtml:135` (тег `<li>`) и Razor-блок `@{...}` перед ним

**Interfaces:**
- Produces: на `li.tree-node` — `data-ds-id` (всегда, GUID датасорса) и `data-tree-role` со значениями `datasource` | `database` | `schema` (у прочих узлов атрибут отсутствует). Task 2 читает `li.dataset.dsId`, `li.dataset.treeRole`, `li.dataset.name`, `li.dataset.database`.

- [ ] **Step 1: Вычислить роль узла**

В Razor-блок вычислений (`@{...}` в начале файла, рядом с `var expandable = ...`) добавить:

```csharp
    // Роль узла для синхронизации с тулбаром (app.js): клик по датасорсу, базе или
    // схеме выставляет селекты редактора. Прочие узлы роли не имеют — клик по таблице
    // не должен дёргать интроспекцию.
    var treeRole = Model.Path == "" ? "datasource"
        : node.Type == DbObjectType.Schema ? "schema"
        : node.Type == DbObjectType.Database ? "database"
        : null;
```

Перед этим сверься с фактическими значениями: открой `src/WebDbViewer.Web/Pages/Shared/TreeNodeVm.cs` и убедись, что `Path` и `Node.Type` называются так; в `src/WebDbViewer.Core/Models.cs` — что enum содержит `DbObjectType.Schema` и `DbObjectType.Database`. Если узел базы PostgreSQL кодируется иначе (например, флагом `Model.AsDatabase`) — используй фактический признак и зафиксируй это в отчёте.

- [ ] **Step 2: Добавить атрибуты в `<li>`**

Строка 135, к существующим атрибутам добавить два:

```html
<li class="tree-node @(node.IsSystem ? "system" : null)" data-type="@node.Type" data-name="@node.Name" data-schema="@node.Schema" data-database="@Model.Database" data-ds-id="@Model.DsId" data-tree-role="@treeRole">
```

Razor опускает атрибут при `null`-значении — у таблиц и папок `data-tree-role` в разметке не появится.

- [ ] **Step 3: Собрать веб-проект в изолированный каталог**

```bash
dotnet build src/WebDbViewer.Web/DbViewer.App.csproj -o "$env:TEMP\wdb-tree"
```

Ожидается: Build succeeded, 0 errors. (Задача 3 запустит exe из этого каталога.)

- [ ] **Step 4: Коммит**

```bash
git add src/WebDbViewer.Web/Pages/Shared/_TreeNode.cshtml
git commit -m "feat: expose datasource id and sync role on tree nodes"
```

---

### Task 2: Очередь синхронизации и общая `refreshMetadata`

**Files:**
- Modify: `frontend/src/app.js:375-422` (обработчик кнопки refresh — заменяется) и конец файла

**Interfaces:**
- Consumes: атрибуты `data-ds-id` / `data-tree-role` / `data-name` / `data-database` на `li.tree-node` (Task 1); селекты `[data-role="datasource-select"|"database-select"|"schema-select"]`; `window.WebDbCompletion.reset/load`; `window.WebDb.toast`.
- Produces: функция `refreshMetadata(dsId, db, schema)` (модульная, наружу не экспортируется — оба потребителя в этом же файле).

- [ ] **Step 1: Заменить блок обработчика кнопки (строки 375–422) целиком на:**

```js
// --- Обновление метаданных схемы ---
// Одна реализация на двух потребителей: кнопку «Перечитать…» в тулбаре и
// синхронизацию из дерева (ниже). Вторая копия правила запрещена — копии расходятся.
let refreshInFlight = false;

async function refreshMetadata(dsId, db, schema) {
  // Повторный вызов во время запроса игнорируется: кнопка могла быть перерисована
  // HTMX-свопом, поэтому блокировка живёт здесь, а aria-disabled — лишь её отражение.
  if (!dsId || refreshInFlight) return;
  refreshInFlight = true;
  const btn = document.querySelector('[data-action="refresh-metadata"]');
  if (btn) btn.setAttribute('aria-disabled', 'true');
  try {
    const res = await fetch('/api/metadata/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ dsId, schema, db }),
    });
    if (!res.ok) {
      // Сервер отказал (400 — не задан датасорс, 404 — датасорс не найден и т.п.):
      // клиентский кэш не трогаем, показываем отдельный тост об ошибке.
      if (window.WebDb && typeof window.WebDb.toast === 'function') {
        window.WebDb.toast(`Не удалось обновить метаданные (${res.status})`, 'error');
      }
      return;
    }
    if (window.WebDbCompletion) {
      window.WebDbCompletion.reset(dsId, db, schema);
      await window.WebDbCompletion.load(dsId, db, schema);
    }
    if (window.WebDb && typeof window.WebDb.toast === 'function') {
      window.WebDb.toast('Метаданные схемы обновляются', 'info');
    }
  } catch (_) {
    if (window.WebDb && typeof window.WebDb.toast === 'function') {
      window.WebDb.toast('Не удалось обновить метаданные', 'error');
    }
  } finally {
    refreshInFlight = false;
    const b = document.querySelector('[data-action="refresh-metadata"]');
    if (b) b.removeAttribute('aria-disabled');
  }
}

// Кнопка в тулбаре: берёт текущие значения селектов. Делегирование на document —
// кнопка живёт в HTMX-фрагменте и пересоздаётся при каждой смене датасорса или базы.
document.addEventListener('click', (e) => {
  const btn = e.target.closest ? e.target.closest('[data-action="refresh-metadata"]') : null;
  if (!btn || btn.getAttribute('aria-disabled') === 'true') return;
  e.preventDefault();
  const ds = document.querySelector('[data-role="datasource-select"]');
  const schemaSelect = document.querySelector('[data-role="schema-select"]');
  const dbSelect = document.querySelector('[data-role="database-select"]');
  refreshMetadata(
    ds && ds.value,
    dbSelect && dbSelect.value ? dbSelect.value : null,
    schemaSelect && schemaSelect.value ? schemaSelect.value : null);
});
```

Обрати внимание: порядок аргументов `refreshMetadata(dsId, db, schema)`, а тело запроса — `{ dsId, schema, db }`, как в текущем коде (сервер принимает `MetadataRefreshRequest(DsId, Schema, Db)`).

- [ ] **Step 2: Добавить очередь синхронизации из дерева (в конец файла)**

```js
// --- Синхронизация дерева с тулбаром ---
// Клик по датасорсу/базе/схеме в навигаторе выставляет селекты редактора и
// обновляет метаданные (решение владельца: refresh при каждом клике).
// Свопы тулбара асинхронные и каскадные (смена датасорса перерисовывает
// #editor-scope, смена базы — ещё раз), поэтому цель применяется пошагово:
// один шаг — одно изменение селекта, продолжение — по htmx:afterSwap.
let treeSync = null; // { dsId, db, schema } — цель незавершённой синхронизации

function applyTreeSync() {
  if (!treeSync) return;
  const goal = treeSync;
  const ds = document.querySelector('[data-role="datasource-select"]');
  if (!ds) { treeSync = null; return; }

  if (ds.value !== goal.dsId) {
    // Датасорса нет в селекте — синхронизировать не с чем, очередь снимается молча.
    if (![...ds.options].some((o) => o.value === goal.dsId)) { treeSync = null; return; }
    ds.value = goal.dsId;
    ds.dispatchEvent(new Event('change', { bubbles: true }));
    return; // продолжение — по htmx:afterSwap на #editor-scope
  }

  const dbSel = document.querySelector('[data-role="database-select"]');
  if (goal.db && dbSel && dbSel.value !== goal.db) {
    if (![...dbSel.options].some((o) => o.value === goal.db)) { treeSync = null; return; }
    dbSel.value = goal.db;
    dbSel.dispatchEvent(new Event('change', { bubbles: true }));
    return; // смена базы перерисует тулбар ещё раз
  }
  if (goal.db && !dbSel) { treeSync = null; return; } // селекта баз нет — цель недостижима

  if (goal.schema) {
    const scSel = document.querySelector('[data-role="schema-select"]');
    if (!scSel || ![...scSel.options].some((o) => o.value === goal.schema)) { treeSync = null; return; }
    if (scSel.value !== goal.schema) {
      scSel.value = goal.schema;
      scSel.dispatchEvent(new Event('change', { bubbles: true })); // тулбар не перерисовывает
    }
  }

  treeSync = null;
  refreshMetadata(goal.dsId, goal.db, goal.schema);
}

document.addEventListener('htmx:afterSwap', (e) => {
  if (e.target && e.target.id === 'editor-scope') applyTreeSync();
});

// Клик по узлу дерева. Без preventDefault/stopPropagation: раскрытие узла (hx-get)
// и прочие обработчики работают как раньше, синхронизация только наблюдает.
// Кнопки действий узла (DDL, данные) сами зовут stopPropagation — сюда не доходят.
document.addEventListener('click', (e) => {
  const label = e.target.closest ? e.target.closest('#nav-tree-root .tree-label') : null;
  if (!label) return;
  const li = label.closest('li[data-tree-role]');
  if (!li || !li.dataset.dsId) return;

  const role = li.dataset.treeRole;
  const target = { dsId: li.dataset.dsId, db: null, schema: null };
  if (role === 'database') target.db = li.dataset.name;
  if (role === 'schema') {
    target.db = li.dataset.database || null;
    target.schema = li.dataset.name;
  }
  // Новый клик замещает незавершённую очередь предыдущего.
  treeSync = target;
  applyTreeSync();
});
```

- [ ] **Step 3: Собрать бандл**

```bash
npm --prefix frontend run build
```

Ожидается: сборка без ошибок.

- [ ] **Step 4: Коммит**

```bash
git add frontend/src/app.js src/WebDbViewer.Web/wwwroot/js/app.js
git commit -m "feat: sync toolbar selects from navigator tree with auto metadata refresh"
```

---

### Task 3: Браузерная проверка

**Files:** нет (проверка; при находках — фиксы в файлы задач 1–2).

- [ ] **Step 1: Поднять собственный экземпляр из изолированного каталога**

Экземпляр владельца (VS) держит `bin/` и не подхватит `.cshtml`. Поэтому:

```bash
dotnet build src/WebDbViewer.Web/DbViewer.App.csproj -o "$env:TEMP\wdb-tree"
```

```bash
& "$env:TEMP\wdb-tree\DbViewer.App.exe" --urls http://localhost:5199 --contentRoot C:\Works\webdbviewer\src\WebDbViewer.Web
```

(запуск в фоне; запомнить PID и в конце убить только его). Вход admin/admin.

- [ ] **Step 2: Пройти критерии спеки**

| Действие в дереве | Ожидание |
|---|---|
| Клик по датасорсу `token` | `#editor-ds` = token; в логе `POST /api/metadata/refresh → 202` |
| Клик по схеме `DICO` под `token` | `#editor-schema` = DICO; refresh; автодополнение — объекты DICO |
| Клик по базе `asl_belgi_db` (PostgreSQL) | `#editor-ds` = 192.168.0.213, `#editor-database` = asl_belgi_db; refresh |
| Клик по схеме `public` внутри базы | `#editor-database` = asl_belgi_db, `#editor-schema` = public; refresh; таблицы `sp_*` в подсказках |
| Повторный клик по той же схеме | второй POST refresh в логе |
| Клик по таблице | селекты не меняются, refresh не уходит; открытие данных работает |
| Раскрытие узла | работает как раньше |
| Кнопка «Перечитать…» | работает (та же функция) |

- [ ] **Step 3: Остановить свой экземпляр** (только свой PID), убедиться `git status` чист, находки — чинить и повторять.

## Итог

| Задача | Результат |
|---|---|
| 1 | `data-ds-id` + `data-tree-role` на узлах дерева |
| 2 | очередь синхронизации + единая `refreshMetadata` |
| 3 | браузерная проверка критериев |
