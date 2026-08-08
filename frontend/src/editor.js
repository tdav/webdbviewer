// SQL-редактор на CodeMirror 6.
// Инициализирует все textarea[data-sql-editor]:
//   data-ds-id       — идентификатор датасорса (обязателен для выполнения/автодополнения)
//   data-session-id  — существующая сессия БД (опционально, обновляется после выполнения)
//   data-dialect     — "postgres" (по умолчанию) | "oracle"
//   data-grid-target — CSS-селектор грида результатов (опционально)
// Горячие клавиши: Ctrl+Enter — statement под курсором, Alt+X — весь скрипт.
import { EditorView, keymap } from '@codemirror/view';
import { EditorState, Compartment, Prec } from '@codemirror/state';
import { basicSetup } from 'codemirror';
import { sql, PostgreSQL, PLSQL } from '@codemirror/lang-sql';
import { autocompletion } from '@codemirror/autocomplete';

const COMPLETION_DEBOUNCE_MS = 250;

// --- Тёмная тема (минимальная, без внешних зависимостей — air-gapped) ---
const darkTheme = EditorView.theme({
  '&': { backgroundColor: '#1e1e2e', color: '#dcdfe4' },
  '.cm-content': { caretColor: '#dcdfe4' },
  '.cm-cursor, .cm-dropCursor': { borderLeftColor: '#dcdfe4' },
  '&.cm-focused .cm-selectionBackground, .cm-selectionBackground': { backgroundColor: '#3b4261' },
  '.cm-activeLine': { backgroundColor: '#2a2a3c' },
  '.cm-gutters': { backgroundColor: '#181825', color: '#6c7086', border: 'none' },
  '.cm-activeLineGutter': { backgroundColor: '#2a2a3c' },
  '.cm-tooltip': { backgroundColor: '#181825', color: '#dcdfe4', border: '1px solid #313244' },
  '.cm-tooltip-autocomplete ul li[aria-selected]': { backgroundColor: '#3b4261' },
}, { dark: true });

const lightTheme = EditorView.theme({}, { dark: false });

function isDarkTheme() {
  const b = document.body;
  return !!b && (b.classList.contains('theme-dark') || b.classList.contains('dark') || b.dataset.theme === 'dark');
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
    const word = context.matchBefore(/[\w"$.]*/);
    if (!context.explicit && (!word || word.from === word.to)) return null;
    const dsId = textarea.dataset.dsId;
    if (!dsId) return null;

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
            }),
            signal: controller.signal,
          });
          if (!res.ok) { resolve(null); return; }
          const data = await res.json();
          const items = Array.isArray(data) ? data : (data.items || []);
          resolve({
            from: word ? word.from : context.pos,
            options: items.map((it) => ({
              label: it.label,
              type: KIND_MAP[it.kind] || 'text',
              apply: it.insertText || undefined,
              detail: it.detail || undefined,
              info: it.documentation || undefined,
              boost: -(it.sortPriority || 0), // меньший SortPriority — выше в списке
            })),
            validFor: /^[\w"$.]*$/,
          });
        } catch (e) {
          resolve(null); // AbortError и сетевые ошибки — просто без подсказок
        }
      }, COMPLETION_DEBOUNCE_MS);
    });
  };
}

// --- Выполнение SQL ---
async function executeSql(view, textarea, wholeScript) {
  const dsId = textarea.dataset.dsId;
  if (!dsId) { toast('Не выбран датасорс для выполнения запроса.'); return true; }
  syncToTextarea(view, textarea);

  const body = {
    dsId,
    sql: view.state.doc.toString(),
    sessionId: textarea.dataset.sessionId || null,
    caretOffset: view.state.selection.main.head,
    wholeScript: !!wholeScript,
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
    if (data.sessionId) textarea.dataset.sessionId = data.sessionId;
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
const views = []; // все живые редакторы — для переключения темы

function currentThemeExt() {
  return isDarkTheme() ? darkTheme : lightTheme;
}

function initEditor(textarea) {
  if (textarea.dataset.cmInitialized === '1') return; // не пересоздавать существующие
  textarea.dataset.cmInitialized = '1';

  const dialect = (textarea.dataset.dialect || '').toLowerCase().includes('oracle') ? PLSQL : PostgreSQL;

  const runStatement = (view) => { executeSql(view, textarea, false); return true; };
  const runScript = (view) => { executeSql(view, textarea, true); return true; };

  const state = EditorState.create({
    doc: textarea.value,
    extensions: [
      basicSetup,
      sql({ dialect, upperCaseKeywords: true }),
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

// Инициализация: при загрузке страницы и после HTMX-свопов.
document.addEventListener('DOMContentLoaded', () => initAll(document));
document.addEventListener('htmx:load', (e) => initAll(e.target || document));
document.addEventListener('htmx:afterSwap', (e) => initAll(e.target || document));

// Переключение темы: кастомное событие (app.js) + наблюдение за классом body.
document.addEventListener('webdb:theme-changed', applyTheme);
if (document.body) {
  new MutationObserver(applyTheme).observe(document.body, { attributes: true, attributeFilter: ['class', 'data-theme'] });
} else {
  document.addEventListener('DOMContentLoaded', () => {
    new MutationObserver(applyTheme).observe(document.body, { attributes: true, attributeFilter: ['class', 'data-theme'] });
  });
}

// Публичный API для других модулей/страниц.
window.WebDbEditor = { initAll, initEditor, syncAll, applyTheme };
