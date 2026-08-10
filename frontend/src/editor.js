// SQL-редактор на CodeMirror 6.
// Инициализирует все textarea[data-sql-editor]:
//   data-ds-id       — идентификатор датасорса (обязателен для выполнения/автодополнения)
//   data-session-id  — существующая сессия БД (опционально, обновляется после выполнения)
//   data-dialect     — "postgres" (по умолчанию) | "oracle"
//   data-grid-target — CSS-селектор грида результатов (опционально)
// Горячие клавиши: Ctrl+Enter — statement под курсором, Alt+X — весь скрипт.
// Кнопки тулбара: [data-action="run"|"run-script"|"cancel"] — обрабатываются здесь же
// (делегирование на document, поэтому переживают HTMX-свопы).
import { EditorView, keymap, showTooltip } from '@codemirror/view';
import { EditorState, Compartment, Prec, StateField, StateEffect } from '@codemirror/state';
import { basicSetup } from 'codemirror';
import { sql, PostgreSQL, PLSQL } from '@codemirror/lang-sql';
import { autocompletion, startCompletion, completionStatus } from '@codemirror/autocomplete';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags } from '@lezer/highlight';
import './completion-schema.js';

const COMPLETION_DEBOUNCE_MS = 250;

// --- Тема редактора (без внешних зависимостей — air-gapped) ---
// Цвета не задаются здесь: редактор читает те же токены, что и остальное
// приложение (app.css). Обе темы отличаются только флагом dark, который
// CodeMirror использует для собственных решений о подсветке.
const editorThemeSpec = {
  '&': { backgroundColor: 'var(--bg)', color: 'var(--text)' },
  '.cm-content': { caretColor: 'var(--text)', fontFamily: 'var(--mono)' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--accent)' },
  '&.cm-focused .cm-selectionBackground, .cm-selectionBackground, ::selection': {
    backgroundColor: 'var(--accent-soft)',
  },
  '.cm-activeLine': { backgroundColor: 'var(--line-highlight)' },
  '.cm-gutters': {
    backgroundColor: 'var(--bg)',
    color: 'var(--text-muted)',
    border: 'none',
    borderRight: '1px solid var(--border)',
  },
  '.cm-activeLineGutter': { backgroundColor: 'var(--line-highlight)', color: 'var(--text)' },
  // Автодополнение парит над контентом — единственное место, где тень уместна.
  '.cm-tooltip': {
    backgroundColor: 'var(--bg-panel)',
    color: 'var(--text)',
    border: '1px solid var(--border-strong)',
    borderRadius: 'var(--radius-md)',
    boxShadow: 'var(--shadow-overlay-sm)',
  },
  '.cm-tooltip-autocomplete ul li[aria-selected]': {
    backgroundColor: 'var(--accent-soft)',
    color: 'var(--text)',
  },
  '.cm-selectionMatch': { backgroundColor: 'var(--accent-soft)' },
  // Подсказка параметров функции: та же плашка, что у автодополнения (.cm-tooltip выше).
  '.cm-signature-help': { padding: '4px 8px', maxWidth: '48ch' },
  '.cm-signature-label': { fontFamily: 'var(--mono)', whiteSpace: 'pre-wrap' },
  '.cm-signature-doc': { marginTop: '2px', color: 'var(--text-muted)', fontSize: '0.9em' },
};

const darkTheme = EditorView.theme(editorThemeSpec, { dark: true });
const lightTheme = EditorView.theme(editorThemeSpec, { dark: false });

// Подсветка SQL. Собственный стиль обязателен: defaultHighlightStyle из
// basicSetup рассчитан на светлый фон и в тёмной теме даёт тёмно-фиолетовые
// ключевые слова на тёмно-сером — примерно 1.5:1. Цвета живут в токенах,
// поэтому один стиль обслуживает обе темы.
const sqlHighlight = HighlightStyle.define([
  { tag: [tags.keyword, tags.operatorKeyword, tags.modifier], color: 'var(--syntax-keyword)', fontWeight: '500' },
  { tag: [tags.string, tags.special(tags.string)], color: 'var(--syntax-string)' },
  { tag: [tags.number, tags.bool, tags.null], color: 'var(--syntax-number)' },
  { tag: [tags.comment, tags.lineComment, tags.blockComment], color: 'var(--syntax-comment)', fontStyle: 'italic' },
  // Имена таблиц, колонок, функций и пунктуация — основным цветом текста:
  // в SQL это и есть содержание запроса, подкрашивать его не нужно.
  { tag: [tags.variableName, tags.propertyName, tags.typeName, tags.function(tags.variableName)], color: 'var(--text)' },
  { tag: [tags.operator, tags.punctuation, tags.separator], color: 'var(--text-muted)' },
]);

// Тема живёт на <html data-theme>, как и во всём приложении. Раньше проверялся
// body.theme-dark, которого разметка не выставляет, и редактор всегда считал
// тему светлой.
function isDarkTheme() {
  return document.documentElement.getAttribute('data-theme') === 'dark';
}

function toast(message) {
  if (window.WebDb && typeof window.WebDb.toast === 'function') window.WebDb.toast(message, 'error');
  else console.error(message);
}

// --- Автодополнение через HTTP: POST /api/completion {dsId, sql, caretOffset} ---
// Debounce 250 мс + отмена устаревших запросов через AbortController.
const KIND_MAP = {
  keyword: 'keyword',
  table: 'class',
  view: 'class',
  column: 'property',
  schema: 'namespace',
  function: 'function',
  constant: 'constant',
  type: 'type',
  alias: 'variable',
  snippet: 'text',
};

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
        // Ответ мог прийти уже после того, как пользователь закрыл попап (Escape)
        // или увёл каретку в другое место — тогда список не должен открываться
        // заново без спроса. Проверяем, что попап всё ещё открыт и каретка
        // осталась там же, где был отправлен запрос.
        if (view && completionStatus(view.state) === 'active' && answerKey({ pos: view.state.selection.main.head, state: view.state }) === key) {
          startCompletion(view);
        }
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

// --- Подсказка параметров функции (signature help) ---
// Показывается, пока каретка внутри скобок известного вызова. Что именно известно,
// решает сервер: у него и кэш метаданных схемы, и справочник встроенных функций.
const SIGNATURE_DEBOUNCE_MS = 150;
const SIGNATURE_LOOKBEHIND = 2000; // дальше этого назад вызов искать бессмысленно

const setSignature = StateEffect.define();

const signatureField = StateField.define({
  create: () => null,
  update(value, tr) {
    for (const e of tr.effects) {
      if (e.is(setSignature)) return e.value;
    }
    // До прихода свежего ответа держим подсказку на месте, двигая позицию с текстом.
    if (value && tr.docChanged) return { ...value, pos: tr.changes.mapPos(value.pos) };
    return value;
  },
  provide: (f) => showTooltip.from(f, (v) => (v ? {
    pos: v.pos,
    above: true,
    create: () => {
      const dom = document.createElement('div');
      dom.className = 'cm-signature-help';
      const label = document.createElement('div');
      label.className = 'cm-signature-label';
      label.textContent = v.label;
      dom.appendChild(label);
      if (v.documentation) {
        const doc = document.createElement('div');
        doc.className = 'cm-signature-doc';
        doc.textContent = v.documentation;
        dom.appendChild(doc);
      }
      return { dom };
    },
  } : null)),
});

/// Смещение открывающей скобки вызова, внутри которого стоит конец текста, иначе -1.
/// Дублирует серверный разбор намеренно: он отсекает запросы там, где вызова заведомо нет.
function openParenBefore(text) {
  let depth = 0;
  for (let i = text.length - 1; i >= 0; i--) {
    const c = text[i];
    if (c === ';') return -1; // граница statement — дальше не смотрим
    if (c === ')') depth++;
    else if (c === '(') {
      if (depth === 0) return i;
      depth--;
    }
  }
  return -1;
}

function makeSignatureListener(textarea) {
  let timer = null;
  let controller = null;

  function clear(view) {
    if (view.state.field(signatureField, false)) {
      // dispatch во время обновления запрещён — выходим из текущего цикла.
      setTimeout(() => view.dispatch({ effects: setSignature.of(null) }), 0);
    }
  }

  return EditorView.updateListener.of((update) => {
    if (!update.docChanged && !update.selectionSet) return;
    const view = update.view;
    const pos = view.state.selection.main.head;
    const before = view.state.doc.sliceString(Math.max(0, pos - SIGNATURE_LOOKBEHIND), pos);
    const open = openParenBefore(before);
    // Скобка без имени слева — группировка выражения, а не вызов.
    const isCall = open >= 0 && /[\w"$#]\s*$/.test(before.slice(0, open));

    if (timer) clearTimeout(timer);
    if (!isCall || !textarea.dataset.dsId || !isPrimaryDatabaseSelected()) {
      clear(view);
      return;
    }

    timer = setTimeout(async () => {
      if (controller) controller.abort();
      controller = new AbortController();
      try {
        const res = await fetch('/api/completion/signature', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            dsId: textarea.dataset.dsId,
            sql: view.state.doc.toString(),
            caretOffset: pos,
            defaultSchema: currentSchema(),
          }),
          signal: controller.signal,
        });
        // 204 — каретка не в известном вызове.
        if (res.status !== 200) { clear(view); return; }
        const data = await res.json();
        view.dispatch({ effects: setSignature.of({ pos, label: data.label, documentation: data.documentation }) });
      } catch (e) {
        // AbortError и сетевые ошибки — просто без подсказки.
      }
    }, SIGNATURE_DEBOUNCE_MS);
  });
}

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

// --- Текущая схема из тулбара (используется автодополнением) ---
function currentSchema() {
  const select = document.querySelector('[data-role="schema-select"]');
  return select && select.value ? select.value : null;
}

// --- Текущая база данных из тулбара (null — база из настроек подключения) ---
function currentDatabase() {
  const select = document.querySelector('[data-role="database-select"]');
  return select && select.value ? select.value : null;
}

// --- Выбрана ли база из настроек подключения (для неё есть кэш метаданных) ---
function isPrimaryDatabaseSelected() {
  const select = document.querySelector('[data-role="database-select"]');
  if (!select || !select.selectedOptions.length) return true; // селекта баз нет — база одна
  return select.selectedOptions[0].dataset.primary === 'true';
}

// --- Выполнение SQL ---
let lastExecutionId = null;

// Таблица-источник строк результата: правка в панели результатов возможна только
// для простого SELECT из одной таблицы. Джойн, объединение, подзапрос, алиас —
// строка результата уже не соответствует строке таблицы, и правка выключается.
// ponytail: разбор регуляркой. Полный парсер здесь не окупается — непонятый
// запрос просто остаётся нередактируемым, а не правится наугад.
const SIMPLE_SELECT =
  /^select\s+[\s\S]+?\sfrom\s+(?:("(?:[^"]|"")+"|[\w$#]+)\s*\.\s*)?("(?:[^"]|"")+"|[\w$#]+)\s*(?:(?:where|order\s+by|group\s+by|having|limit|offset|fetch|for)\b[\s\S]*)?$/i;

function detectSourceTable(sql, dialect) {
  if (!sql) return null;
  const text = sql
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/--[^\n]*/g, ' ')
    .trim()
    .replace(/;+$/, '')
    .trim();
  if (/\b(join|union|except|intersect)\b/i.test(text)) return null;
  // Второй select — подзапрос: FROM ниже относился бы уже не к нему.
  if ((text.match(/\bselect\b/gi) || []).length > 1) return null;
  const m = SIMPLE_SELECT.exec(text);
  if (!m) return null;
  // Регистр идентификатора без кавычек сервер сворачивает сам: Oracle — к верхнему,
  // PostgreSQL — к нижнему. В каталог мы идём точным именем, поэтому делаем это здесь.
  const oracle = (dialect || '').toLowerCase().includes('oracle');
  const name = (id) => (id.startsWith('"')
    ? id.slice(1, -1).replace(/""/g, '"')
    : (oracle ? id.toUpperCase() : id.toLowerCase()));
  return { schema: m[1] ? name(m[1]) : null, table: name(m[2]) };
}

async function executeSql(view, textarea, wholeScript) {
  const dsId = textarea.dataset.dsId;
  if (!dsId) { toast('Не выбран датасорс для выполнения запроса.'); return true; }
  syncToTextarea(view, textarea);

  const database = currentDatabase();
  const body = {
    dsId,
    sql: view.state.doc.toString(),
    // Смена базы в тулбаре — другая сессия: старый sessionId к ней не относится.
    sessionId: textarea.dataset.sessionDb === (database || '') ? textarea.dataset.sessionId || null : null,
    caretOffset: view.state.selection.main.head,
    wholeScript: !!wholeScript,
    db: database,
  };
  try {
    const res = await fetch('/api/query/execute', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      let msg = 'Ошибка выполнения запроса.';
      try { const err = await res.json(); if (err && err.error) msg = err.error; } catch (_) { /* не JSON */ }
      toast(msg);
      return true;
    }
    const data = await res.json();
    if (data.sessionId) {
      textarea.dataset.sessionId = data.sessionId;
      textarea.dataset.sessionDb = database || '';
    }
    lastExecutionId = data.executionId;
    setRunningState(true);
    // Схема в запросе может быть не указана — тогда берём выбранную в тулбаре:
    // именно в ней сервер и разрешит имя таблицы.
    const source = detectSourceTable(data.statementSql, textarea.dataset.dialect);
    const schema = source && (source.schema || currentSchema());
    // Грид (grid.js) подхватывает событие и подключается к SSE-стриму.
    document.dispatchEvent(new CustomEvent('webdb:execute', {
      detail: {
        executionId: data.executionId,
        dsId,
        gridTarget: textarea.dataset.gridTarget || null,
        // Источник строк для правки результата; null — запрос сложнее простого SELECT.
        source: schema
          ? { dsId, db: database, schema, table: source.table, dialect: textarea.dataset.dialect || '' }
          : null,
      },
    }));
  } catch (e) {
    toast('Сеть недоступна: не удалось отправить запрос.');
  }
  return true;
}

function syncToTextarea(view, textarea) {
  textarea.value = view.state.doc.toString();
}

// --- Инициализация ---
const themeCompartment = new Compartment();
const dialectCompartment = new Compartment(); // диалект меняется при смене датасорса
const views = []; // все живые редакторы — для переключения темы

function currentThemeExt() {
  return isDarkTheme() ? darkTheme : lightTheme;
}

/// Расширение подсветки/грамматики по имени диалекта датасорса.
function dialectExtension(name) {
  const dialect = (name || '').toLowerCase().includes('oracle') ? PLSQL : PostgreSQL;
  return sql({ dialect, upperCaseKeywords: true });
}

function initEditor(textarea) {
  if (textarea.dataset.cmInitialized === '1') return; // не пересоздавать существующие
  textarea.dataset.cmInitialized = '1';

  const dialectExt = dialectExtension(textarea.dataset.dialect);

  const runStatement = (view) => { executeSql(view, textarea, false); return true; };
  const runScript = (view) => { executeSql(view, textarea, true); return true; };

  const state = EditorState.create({
    doc: textarea.value,
    extensions: [
      basicSetup,
      // Идёт после basicSetup: его defaultHighlightStyle подключён как fallback
      // и уступает любому явно заданному стилю.
      syntaxHighlighting(sqlHighlight),
      dialectCompartment.of(dialectExt),
      autocompletion({ override: [makeCompletionSource(textarea)] }),
      signatureField,
      makeSignatureListener(textarea),
      themeCompartment.of(currentThemeExt()),
      Prec.highest(keymap.of([
        { key: 'Ctrl-Enter', mac: 'Cmd-Enter', run: runStatement, preventDefault: true },
        { key: 'Alt-x', run: runScript, preventDefault: true },
      ])),
      // Синхронизация в textarea при каждом изменении — чтобы HTMX-формы всегда были актуальны.
      EditorView.updateListener.of((u) => { if (u.docChanged) textarea.value = u.state.doc.toString(); }),
    ],
  });

  const wrapper = document.createElement('div');
  wrapper.className = 'wdb-editor';
  textarea.insertAdjacentElement('afterend', wrapper);
  const view = new EditorView({ state, parent: wrapper });
  textarea.style.display = 'none';
  views.push({ view, textarea });

  loadSchemaMap(textarea.dataset.dsId, currentSchema());

  // Перед submit формы — синхронизация значения в скрытый textarea.
  const form = textarea.closest('form');
  if (form && form.dataset.wdbEditorSyncBound !== '1') {
    form.dataset.wdbEditorSyncBound = '1';
    form.addEventListener('submit', () => syncAll(form));
    form.addEventListener('htmx:configRequest', () => syncAll(form));
  }

  // Открытие данных таблицы: вкладка приходит с готовым SELECT и выполняется сразу.
  // Атрибут снимается, иначе повторный initAll (htmx:load после swap) запустил бы
  // тот же запрос второй раз.
  if (textarea.dataset.autorun === '1') {
    delete textarea.dataset.autorun;
    selectDatabase(textarea.dataset.database);
    executeSql(view, textarea, false);
  }
}

// Таблица может лежать в базе, отличной от выбранной в тулбаре: без переключения
// запрос ушёл бы в текущую базу и вернул «relation does not exist».
function selectDatabase(name) {
  if (!name) return;
  const select = document.querySelector('[data-role="database-select"]');
  if (!select || select.value === name) return;
  if (![...select.options].some((o) => o.value === name)) return;
  select.value = name;
  // change подхватывает htmx на самом селекте — он перечитывает список схем.
  select.dispatchEvent(new Event('change', { bubbles: true }));
}

function syncAll(root) {
  for (const { view, textarea } of views) {
    if (!root || root.contains(textarea)) syncToTextarea(view, textarea);
  }
}

function initAll(root) {
  const scope = root && root.querySelectorAll ? root : document;
  scope.querySelectorAll('textarea[data-sql-editor]').forEach(initEditor);
}

function applyTheme() {
  const ext = currentThemeExt();
  for (const { view } of views) {
    view.dispatch({ effects: themeCompartment.reconfigure(ext) });
  }
}

// --- Кнопки тулбара (▶ Выполнить, ▶▶ Выполнить скрипт, ■ Отмена) ---

/// Редактор активной вкладки: панель .editor-pane.active, иначе единственный редактор на странице.
function activeEditor() {
  const pane = document.querySelector('.editor-pane.active');
  const textarea = (pane && pane.querySelector('textarea[data-sql-editor]'))
    || document.querySelector('textarea[data-sql-editor]');
  if (!textarea) return null;
  return views.find((v) => v.textarea === textarea) || null;
}

/// Кнопка остановки активна только пока запрос выполняется.
/// Помечается aria-disabled, а не атрибутом disabled: кнопка иконочная, и в
/// отключённом виде она обязана оставаться наводимой и фокусируемой — иначе её
/// назначение узнать неоткуда. Клик при этом блокируется в обработчике ниже.
function setRunningState(running) {
  document.querySelectorAll('[data-action="cancel"]').forEach((b) => {
    b.setAttribute('aria-disabled', String(!running));
  });
  const status = document.querySelector('[data-role="results-status"]');
  if (!status) return;
  status.replaceChildren();
  if (!running) {
    status.textContent = 'Готово';
    return;
  }
  // «Выполняется» — единственное состояние статусбара, которое длится:
  // кольцо рядом с подписью отличает ожидание от статичного текста.
  const wrap = document.createElement('span');
  wrap.className = 'status-running';
  const ring = document.createElement('span');
  ring.className = 'spinner';
  ring.setAttribute('aria-hidden', 'true');
  wrap.append(ring, document.createTextNode('Выполняется…'));
  status.appendChild(wrap);
}

function runActive(wholeScript) {
  const entry = activeEditor();
  if (!entry) { toast('Редактор SQL не найден на странице.'); return; }
  executeSql(entry.view, entry.textarea, wholeScript);
}

async function cancelActive() {
  if (!lastExecutionId) return;
  try {
    await fetch(`/api/query/cancel/${lastExecutionId}`, { method: 'POST' });
  } catch (_) { /* запрос мог уже завершиться */ }
}

document.addEventListener('click', (e) => {
  const btn = e.target.closest ? e.target.closest('[data-action]') : null;
  // aria-disabled не блокирует клик сам по себе — это делаем здесь.
  if (!btn || btn.disabled || btn.getAttribute('aria-disabled') === 'true') return;
  switch (btn.dataset.action) {
    case 'run': e.preventDefault(); runActive(false); break;
    case 'run-script': e.preventDefault(); runActive(true); break;
    case 'cancel': e.preventDefault(); cancelActive(); break;
  }
});

// Стрим результатов завершился (grid.js) — снимаем состояние «выполняется».
document.addEventListener('webdb:query-finished', () => setRunningState(false));

// Смена датасорса в тулбаре: все редакторы переключаются на новый датасорс,
// сессия предыдущего датасорса больше не годится, диалект может измениться.
document.addEventListener('change', (e) => {
  const select = e.target;
  if (!select || select.dataset === undefined) return;

  // Смена схемы — тот же датасорс, но подсказки пойдут по другому набору объектов.
  if (select.dataset.role === 'schema-select') {
    const editor = activeEditor();
    if (editor) loadSchemaMap(editor.textarea.dataset.dsId, select.value || null);
    return;
  }

  if (select.dataset.role !== 'datasource-select') return;
  const kind = select.selectedOptions[0] ? select.selectedOptions[0].dataset.kind : null;
  const ext = dialectExtension(kind);
  for (const { view, textarea } of views) {
    textarea.dataset.dsId = select.value || '';
    if (kind) textarea.dataset.dialect = kind;
    delete textarea.dataset.sessionId;
    view.dispatch({ effects: dialectCompartment.reconfigure(ext) });
  }
  loadSchemaMap(select.value || '', currentSchema());
});

// Инициализация: при загрузке страницы и после HTMX-свопов.
document.addEventListener('DOMContentLoaded', () => initAll(document));
document.addEventListener('htmx:load', (e) => initAll(e.target || document));
document.addEventListener('htmx:afterSwap', (e) => initAll(e.target || document));

// Переключение темы: кастомное событие (app.js) + наблюдение за <html data-theme>.
// documentElement существует всегда, поэтому ждать DOMContentLoaded не нужно.
document.addEventListener('webdb:theme-changed', applyTheme);
new MutationObserver(applyTheme).observe(document.documentElement, {
  attributes: true,
  attributeFilter: ['data-theme'],
});

// Публичный API для других модулей/страниц.
window.WebDbEditor = { initAll, initEditor, syncAll, applyTheme, runActive, cancelActive };
