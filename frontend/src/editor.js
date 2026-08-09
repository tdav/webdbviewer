// SQL-редактор на CodeMirror 6.
// Инициализирует все textarea[data-sql-editor]:
//   data-ds-id       — идентификатор датасорса (обязателен для выполнения/автодополнения)
//   data-session-id  — существующая сессия БД (опционально, обновляется после выполнения)
//   data-dialect     — "postgres" (по умолчанию) | "oracle"
//   data-grid-target — CSS-селектор грида результатов (опционально)
// Горячие клавиши: Ctrl+Enter — statement под курсором, Alt+X — весь скрипт.
// Кнопки тулбара: [data-action="run"|"run-script"|"cancel"] — обрабатываются здесь же
// (делегирование на document, поэтому переживают HTMX-свопы).
import { EditorView, keymap } from '@codemirror/view';
import { EditorState, Compartment, Prec } from '@codemirror/state';
import { basicSetup } from 'codemirror';
import { sql, PostgreSQL, PLSQL } from '@codemirror/lang-sql';
import { autocompletion } from '@codemirror/autocomplete';
import { HighlightStyle, syntaxHighlighting } from '@codemirror/language';
import { tags } from '@lezer/highlight';

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
  alias: 'variable',
  snippet: 'text',
};

function makeCompletionSource(textarea) {
  let timer = null;
  let controller = null;

  return (context) => {
    // chain — вся цепочка с квалификаторами («schema.», «alias.tbl.»): по ней решаем,
    // просить ли подсказки. word — только начатый идентификатор после последней точки:
    // именно его CodeMirror заменяет вставкой и по нему фильтрует список.
    // Если считать from по chain, то после «schema.» ни один вариант не совпадёт
    // с текстом «schema.» и список окажется пустым.
    const chain = context.matchBefore(/[\w"$.]*/);
    const word = context.matchBefore(/[\w"$]*/);
    if (!context.explicit && (!chain || chain.from === chain.to)) return null;
    const dsId = textarea.dataset.dsId;
    if (!dsId) return null;
    // Кэш метаданных строится только для базы из настроек подключения: в чужой базе
    // подсказки объектов были бы из другой БД — не предлагаем их вовсе.
    if (!isPrimaryDatabaseSelected()) return null;

    return new Promise((resolve) => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(async () => {
        if (controller) controller.abort(); // отмена устаревшего запроса
        controller = new AbortController();
        try {
          const res = await fetch('/api/completion', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              dsId,
              sql: context.state.doc.toString(),
              caretOffset: context.pos,
              defaultSchema: currentSchema(),
            }),
            signal: controller.signal,
          });
          if (!res.ok) { resolve(null); return; }
          const data = await res.json();
          const items = Array.isArray(data) ? data : (data.items || []);
          resolve({
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
          });
        } catch (e) {
          resolve(null); // AbortError и сетевые ошибки — просто без подсказок
        }
      }, COMPLETION_DEBOUNCE_MS);
    });
  };
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
    // Грид (grid.js) подхватывает событие и подключается к SSE-стриму.
    document.dispatchEvent(new CustomEvent('webdb:execute', {
      detail: {
        executionId: data.executionId,
        dsId,
        gridTarget: textarea.dataset.gridTarget || null,
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

  // Перед submit формы — синхронизация значения в скрытый textarea.
  const form = textarea.closest('form');
  if (form && form.dataset.wdbEditorSyncBound !== '1') {
    form.dataset.wdbEditorSyncBound = '1';
    form.addEventListener('submit', () => syncAll(form));
    form.addEventListener('htmx:configRequest', () => syncAll(form));
  }
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
  if (status) status.textContent = running ? 'Выполняется…' : 'Готово';
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
  if (!select || select.dataset === undefined || select.dataset.role !== 'datasource-select') return;
  const kind = select.selectedOptions[0] ? select.selectedOptions[0].dataset.kind : null;
  const ext = dialectExtension(kind);
  for (const { view, textarea } of views) {
    textarea.dataset.dsId = select.value || '';
    if (kind) textarea.dataset.dialect = kind;
    delete textarea.dataset.sessionId;
    view.dispatch({ effects: dialectCompartment.reconfigure(ext) });
  }
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
