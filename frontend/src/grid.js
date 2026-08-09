// Виртуализованный result grid без сторонних библиотек.
// Элементы: <div data-result-grid> — грид результатов запроса (SSE),
//           <div data-result-grid data-mode="table" data-ds-id data-schema data-table> — данные таблицы (keyset-страницы).
// Возможности: рендер только видимых строк, гориз. скролл, сортировка кликом по заголовку (режим table),
// бесконечная прокрутка, NULL серым курсивом, выделение ячеек, Ctrl+C (TSV), статусбар.
// Inline-редактирование (режим table): двойной клик/Enter — редактор ячейки (textarea-попап для многострочных),
// Esc — отмена, NULL-чекбокс, подсветка изменённых ячеек, «+ Строка», удаление выделенных строк,
// панель «Сохранить (N)»/«Отменить» — пакет изменений уходит на POST /api/data/edit.
// Read-only датасорс: атрибут data-readonly="true" на контейнере грида блокирует редактирование.

const ROW_HEIGHT = 28;     // фиксированная высота строки, px
const BUFFER_ROWS = 12;    // буфер видимых строк сверху/снизу
const LOAD_THRESHOLD = 400; // px до низа — момент подгрузки следующей страницы

// Иконки для элементов, которые грид создаёт сам (запасная панель правки,
// попап многострочного значения). Источник истины набора — UiIcons.cs; здесь
// повторены только те, что нужны гриду, чтобы разметка из JS и из Razor
// выглядела одинаково.
const ICON_ATTRS =
  'class="ui-icon" viewBox="0 0 16 16" fill="none" stroke="currentColor" ' +
  'stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"';
const ICONS = {
  save: `<svg ${ICON_ATTRS}><path d="M3.4 2.6h7.2l2.8 2.8v8a1 1 0 0 1-1 1H3.4a1 1 0 0 1-1-1V3.6a1 1 0 0 1 1-1z"/><path d="M5.2 2.6v3.6h5.2V2.6"/><path d="M5.2 14.4v-4.2h5.6v4.2"/></svg>`,
  discard: `<svg ${ICON_ATTRS}><path d="M2.6 4.2v3.6h3.6"/><path d="M3.3 7.6a5.2 5.2 0 1 1 1.2 4.3"/></svg>`,
  addRow: `<svg ${ICON_ATTRS}><path d="M2.4 4.6h11.2M2.4 8h6.2M2.4 11.4h4.4"/><path d="M11.6 9.2v5.2M9 11.8h5.2"/></svg>`,
  deleteRows: `<svg ${ICON_ATTRS}><path d="M2.4 4.6h11.2M2.4 8h6.2M2.4 11.4h4.4"/><path d="M9.8 10 14 14.2M14 10l-4.2 4.2"/></svg>`,
  confirm: `<svg ${ICON_ATTRS}><path d="M2.8 8.6 6.2 12l7-8"/></svg>`,
  close: `<svg ${ICON_ATTRS}><path d="M4 4l8 8M12 4l-8 8"/></svg>`,
};

// --- Стили грида ---
// Грид берёт цвета из общих токенов app.css (:root и html[data-theme]).
// Собственной таблицы тем у него нет намеренно: раньше она ключевалась на
// body.theme-dark, тогда как приложение переключает html[data-theme], и грид
// оставался светлым в тёмной теме. Фолбэки в var() держат грид читаемым,
// если он окажется на странице без app.css.
const GRID_CSS = `
.wdb-grid{
  --wdb-bg:var(--bg-panel,#fff);
  --wdb-fg:var(--text,#1a1a24);
  --wdb-border:var(--border,rgba(26,26,46,.12));
  --wdb-head:var(--bg-panel-alt,#f0f0f4);
  --wdb-sel:var(--bg-active,rgba(255,107,44,.13));
  --wdb-null:var(--text-muted,#606070);
  --wdb-hover:var(--bg-hover,#e4e4ec);
  --wdb-dirty:var(--warning-bg,rgba(180,83,9,.11));
  --wdb-del:var(--danger-bg,rgba(220,38,38,.10));
  --wdb-new:var(--success-bg,rgba(21,128,61,.11));
  --wdb-mono:var(--mono,ui-monospace,Consolas,monospace);
  display:flex;flex-direction:column;border:1px solid var(--wdb-border);
  font:13px/1.3 var(--wdb-mono);height:100%;min-height:120px;
  background:var(--wdb-bg);color:var(--wdb-fg)
}
.wdb-grid-header{display:flex;overflow:hidden;background:var(--wdb-head);border-bottom:1px solid var(--wdb-border);flex:none;user-select:none}
.wdb-grid-hcell{flex:none;width:160px;padding:4px 8px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;border-right:1px solid var(--wdb-border);cursor:default}
.wdb-grid-hcell.sortable{cursor:pointer}
.wdb-grid-hcell .wdb-type{display:block;font-weight:400;font-size:11px;color:var(--wdb-null)}
.wdb-grid-viewport{position:relative;overflow:auto;flex:1;outline:none}
.wdb-grid-spacer{position:absolute;top:0;left:0;width:1px;visibility:hidden}
.wdb-grid-canvas{position:absolute;top:0;left:0;min-width:100%}
.wdb-grid-row{display:flex;height:${ROW_HEIGHT}px;box-sizing:border-box}
.wdb-grid-row:hover{background:var(--wdb-hover)}
.wdb-grid-cell{flex:none;width:160px;padding:4px 8px;box-sizing:border-box;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;border-right:1px solid var(--wdb-border);border-bottom:1px solid var(--wdb-border);cursor:default}
/* Только тонировка, без рамки: при выделении блока рамка на каждой ячейке
   превращает область в сетку коробок вместо одного выделенного блока. */
.wdb-grid-cell.selected{background:var(--wdb-sel)}
.wdb-null{color:var(--wdb-null);font-style:italic}
.wdb-grid-status{position:relative;flex:none;padding:3px 8px;font-size:12px;border-top:1px solid var(--wdb-border);background:var(--wdb-head);color:var(--wdb-null);display:flex;gap:12px;overflow:hidden}
.wdb-grid-status .error{color:var(--danger,#dc2626)}
/* Подгрузка следующей keyset-страницы: бегунок по верхней кромке статусбара.
   Скелет здесь не годится — строки уже есть, ждём только продолжения. */
.wdb-grid-status.loading::after{
  content:"";position:absolute;top:0;left:0;height:2px;width:30%;
  background:var(--accent,#ff6b2c);animation:wdb-grid-slide 1s linear infinite
}
@keyframes wdb-grid-slide{0%{transform:translateX(-100%)}100%{transform:translateX(400%)}}

/* Ожидание первой страницы (или результата запроса): скелет поверх пустого
   тела грида. Шапка ещё не известна, поэтому первая полоса шире остальных. */
/* Скелет перехватывает ввод: пока строк нет, клик по телу грида пришёлся бы
   по данным предыдущей выборки. Тулбар и кнопка остановки остаются доступны —
   блокируется область грида, а не экран. */
.wdb-grid-loading{position:absolute;inset:0;z-index:1;background:var(--wdb-bg);padding:8px;cursor:progress}
.wdb-grid-loading[hidden]{display:none}
.wdb-grid-skrows{display:flex;flex-direction:column;gap:6px}
.wdb-grid-skrow{display:flex;gap:10px}
/* Плашка и блик — полупрозрачные слои (токены app.css): тональный уровень,
   подобранный под панель, внутри грида на своём фоне сливался бы с ним. */
.wdb-sk{position:relative;overflow:hidden;height:14px;border-radius:4px;background:var(--skeleton-bg,rgba(255,255,255,.10))}
.wdb-sk::after{
  content:"";position:absolute;inset:0;transform:translateX(-100%);
  background:linear-gradient(90deg,transparent 0%,var(--skeleton-sheen,rgba(255,255,255,.18)) 45%,var(--skeleton-sheen,rgba(255,255,255,.18)) 55%,transparent 100%);
  animation:wdb-sk-sweep 1.2s ease-in-out infinite
}
@keyframes wdb-sk-sweep{to{transform:translateX(100%)}}
.wdb-grid-skrow .wdb-sk:nth-child(1){flex:0 0 14%}
.wdb-grid-skrow .wdb-sk:nth-child(2){flex:0 0 26%}
.wdb-grid-skrow .wdb-sk:nth-child(3){flex:0 0 18%}
.wdb-grid-skrow .wdb-sk:nth-child(4){flex:1 1 auto}
.wdb-grid-skrow:first-child .wdb-sk{height:16px}
.wdb-grid-skrow:nth-child(3){opacity:.8}
.wdb-grid-skrow:nth-child(4){opacity:.6}
.wdb-grid-skrow:nth-child(5){opacity:.45}
.wdb-grid-skrow:nth-child(6){opacity:.3}
.wdb-grid-skrow:nth-child(7){opacity:.18}
/* Статус загрузки для скринридера: визуально его несёт скелет. */
.wdb-sr{position:absolute;width:1px;height:1px;margin:-1px;overflow:hidden;clip-path:inset(50%);white-space:nowrap}
@media (prefers-reduced-motion: reduce){
  /* Замерший бегунок и замерший блик читаются как дефект отрисовки,
     поэтому здесь меняется вид, а не длительность. */
  .wdb-grid-status.loading::after{animation:none;width:100%;opacity:.6}
  .wdb-sk::after{animation:none;display:none}
  .wdb-sk{background:var(--border-strong,rgba(255,255,255,.16))}
}
.wdb-grid-cell.dirty{background:var(--wdb-dirty);font-weight:600}
.wdb-grid-row.deleted{background:var(--wdb-del);text-decoration:line-through;opacity:.6}
.wdb-grid-row.newrow{background:var(--wdb-new)}
.wdb-cell-editor{display:flex;align-items:center;gap:4px;width:100%;height:100%}
.wdb-cell-editor input[type=text]{flex:1;min-width:0;height:20px;font:inherit;padding:0 4px;border:1px solid var(--accent-line,#c25100);outline:none;background:var(--wdb-bg);color:var(--wdb-fg)}
.wdb-cell-editor label{display:flex;align-items:center;gap:2px;font-size:11px;cursor:pointer;user-select:none}
.wdb-edit-panel{flex:none;display:flex;align-items:center;gap:8px;padding:4px 8px;border-top:1px solid var(--wdb-border);background:var(--wdb-head);font:13px var(--font-ui,system-ui,sans-serif)}
.wdb-edit-panel button{font:12px var(--font-ui,system-ui,sans-serif);padding:3px 10px;cursor:pointer}
.wdb-edit-panel button:disabled{opacity:.5;cursor:default}
.wdb-popup-overlay{position:fixed;inset:0;background:rgba(10,10,14,.58);z-index:var(--z-modal,50);display:flex;align-items:center;justify-content:center}
.wdb-popup{background:var(--wdb-bg);color:var(--wdb-fg);border:1px solid var(--border-strong,rgba(26,26,46,.22));border-radius:var(--radius-xl,12px);box-shadow:var(--shadow-overlay,0 8px 24px rgba(0,0,0,.45));width:560px;max-width:92vw;display:flex;flex-direction:column;font:13px var(--font-ui,system-ui,sans-serif)}
.wdb-popup-title{padding:8px 12px;font-weight:600;border-bottom:1px solid var(--wdb-border)}
.wdb-popup textarea{margin:10px 12px 4px;min-height:180px;resize:vertical;font:13px var(--wdb-mono);background:var(--bg-well,#e9e9f1);color:var(--wdb-fg);border:1px solid var(--wdb-border);border-radius:var(--radius-md,6px);padding:6px 8px}
.wdb-popup-footer{display:flex;align-items:center;gap:8px;padding:8px 12px}
.wdb-popup-footer .spacer{margin-left:auto}
`;

function injectCss() {
  if (document.getElementById('wdb-grid-css')) return;
  const style = document.createElement('style');
  style.id = 'wdb-grid-css';
  style.textContent = GRID_CSS;
  document.head.appendChild(style);
}

// Скелет тела грида: семь полос-строк, верхняя играет роль шапки.
// Разметка строится в JS, чтобы страницы, поднимающие грид (Data.cshtml,
// редактор), не повторяли её у себя.
const SKELETON_ROWS = 7;
const SKELETON_CELLS = 4;

function buildSkeleton() {
  const box = document.createElement('div');
  box.className = 'wdb-grid-loading';
  const sr = document.createElement('span');
  sr.className = 'wdb-sr';
  sr.textContent = 'Загрузка данных…';
  box.appendChild(sr);
  const rows = document.createElement('div');
  rows.className = 'wdb-grid-skrows';
  rows.setAttribute('aria-hidden', 'true');
  for (let r = 0; r < SKELETON_ROWS; r++) {
    const row = document.createElement('div');
    row.className = 'wdb-grid-skrow';
    for (let c = 0; c < SKELETON_CELLS; c++) {
      const cell = document.createElement('div');
      cell.className = 'wdb-sk';
      row.appendChild(cell);
    }
    rows.appendChild(row);
  }
  box.appendChild(rows);
  return box;
}

function toast(message, type) {
  if (window.WebDb && typeof window.WebDb.toast === 'function') window.WebDb.toast(message, type || 'error');
  else if (type === 'info') console.log(message);
  else console.error(message);
}

class VirtualGrid {
  constructor(el) {
    this.el = el;
    this.mode = el.dataset.mode === 'table' ? 'table' : 'query';
    this.columns = [];
    this.rows = [];
    this.truncated = false;
    this.elapsedMs = null;
    this.loading = false;
    this.nextAfter = null;    // keyset-курсор следующей страницы (режим table)
    this.orderBy = null;
    this.desc = false;
    this.eventSource = null;
    this.selAnchor = null;    // {r,c}
    this.selFocus = null;
    this.renderScheduled = false;

    // --- Состояние inline-редактирования (режим table) ---
    this.editable = this.mode === 'table' && el.dataset.readonly !== 'true';
    this.cellEdits = new Map();    // rowIndex -> Map(colIndex -> новое значение) — существующие строки
    this.insertRows = new Set();   // индексы новых (несохранённых) строк
    this.insertTouched = new Map();// rowIndex -> Set(colIndex) — заполненные колонки новой строки
    this.deleteRows = new Set();   // индексы строк, помеченных на удаление
    this.tableMeta = null;         // {readOnly, primaryKey[], rowAddressColumn, columns[]}
    this.activeEditor = null;      // {commit(), cancel()}
    this.saving = false;

    this.buildDom();
    if (this.mode === 'table') this.loadFirstPage();
  }

  buildDom() {
    this.el.classList.add('wdb-grid');
    this.el.innerHTML = '';
    this.header = document.createElement('div');
    this.header.className = 'wdb-grid-header';

    this.viewport = document.createElement('div');
    this.viewport.className = 'wdb-grid-viewport';
    this.viewport.tabIndex = 0;

    this.spacer = document.createElement('div');
    this.spacer.className = 'wdb-grid-spacer';
    this.canvas = document.createElement('div');
    this.canvas.className = 'wdb-grid-canvas';
    // Скелет живёт внутри viewport (у него position:relative) и перекрывает
    // тело грида, пока строк ещё нет. Шапку он не закрывает: когда колонки
    // уже известны, их незачем прятать за плейсхолдером.
    this.skeleton = buildSkeleton();
    this.skeleton.hidden = true;
    this.viewport.append(this.spacer, this.canvas, this.skeleton);

    this.status = document.createElement('div');
    this.status.className = 'wdb-grid-status';

    this.el.append(this.header, this.viewport, this.status);

    this.viewport.addEventListener('scroll', () => {
      // Горизонтальный скролл заголовка синхронно с телом.
      this.header.scrollLeft = this.viewport.scrollLeft;
      this.scheduleRender();
      if (this.mode === 'table') this.maybeLoadMore();
    });
    this.viewport.addEventListener('mousedown', (e) => this.onCellMouseDown(e));
    this.viewport.addEventListener('keydown', (e) => {
      if ((e.ctrlKey || e.metaKey) && (e.key === 'c' || e.key === 'C')) {
        this.copySelection();
        e.preventDefault();
      } else if (e.key === 'Enter' && this.mode === 'table' && this.selFocus && !this.activeEditor) {
        // Enter по выделенной ячейке — начать редактирование.
        this.beginEdit(this.selFocus.r, this.selFocus.c);
        e.preventDefault();
      }
    });
    // Двойной клик по ячейке — начать редактирование.
    this.viewport.addEventListener('dblclick', (e) => {
      const cell = e.target.closest('.wdb-grid-cell');
      if (!cell || this.mode !== 'table') return;
      this.beginEdit(parseInt(cell.dataset.row, 10), parseInt(cell.dataset.col, 10));
    });
    if (this.mode === 'table') this.initEditPanel();
    this.setStatus('Нет данных');
  }

  // ---------------- Данные ----------------

  reset() {
    this.rows = [];
    this.columns = [];
    this.truncated = false;
    this.elapsedMs = null;
    this.nextAfter = null;
    this.selAnchor = this.selFocus = null;
    // Перезагрузка данных инвалидирует индексы строк — сбрасываем несохранённые правки.
    if (this.activeEditor) this.activeEditor = null;
    this.cellEdits.clear();
    this.insertRows.clear();
    this.insertTouched.clear();
    this.deleteRows.clear();
    if (this.editPanel) this.updateEditPanel();
    this.header.innerHTML = '';
    this.canvas.innerHTML = '';
    this.spacer.style.height = '0px';
    this.viewport.scrollTop = 0;
  }

  setColumns(columns) {
    this.columns = columns || [];
    this.header.innerHTML = '';
    this.columns.forEach((col, idx) => {
      const h = document.createElement('div');
      h.className = 'wdb-grid-hcell';
      const name = document.createElement('span');
      name.textContent = col.name + (this.orderBy === col.name ? (this.desc ? ' ▼' : ' ▲') : '');
      const type = document.createElement('span');
      type.className = 'wdb-type';
      type.textContent = col.dataType || '';
      h.append(name, type);
      h.title = col.name + (col.dataType ? ' : ' + col.dataType : '');
      if (this.mode === 'table') {
        // Клик по заголовку — сортировка (перезапрос первой страницы).
        h.classList.add('sortable');
        h.addEventListener('click', () => this.sortBy(col.name));
      }
      h.dataset.col = String(idx);
      this.header.appendChild(h);
    });
  }

  appendRows(batch) {
    if (!batch || !batch.length) return;
    // Пришли данные — скелет больше не нужен, даже если стрим ещё открыт.
    this.setLoading(null);
    this.rows.push(...batch);
    this.spacer.style.height = (this.rows.length * ROW_HEIGHT) + 'px';
    this.scheduleRender();
    this.updateStatus();
  }

  // ---------------- Виртуализованный рендер ----------------

  scheduleRender() {
    if (this.renderScheduled) return;
    this.renderScheduled = true;
    requestAnimationFrame(() => {
      this.renderScheduled = false;
      this.render();
    });
  }

  render() {
    const total = this.rows.length;
    const vh = this.viewport.clientHeight || 300;
    let first = Math.floor(this.viewport.scrollTop / ROW_HEIGHT) - BUFFER_ROWS;
    if (first < 0) first = 0;
    let last = Math.ceil((this.viewport.scrollTop + vh) / ROW_HEIGHT) + BUFFER_ROWS;
    if (last > total) last = total;

    // Рендерим только видимые строки + буфер.
    this.canvas.style.transform = `translateY(${first * ROW_HEIGHT}px)`;
    const frag = document.createDocumentFragment();
    for (let r = first; r < last; r++) {
      frag.appendChild(this.renderRow(r));
    }
    this.canvas.replaceChildren(frag);
  }

  renderRow(r) {
    const rowEl = document.createElement('div');
    rowEl.className = 'wdb-grid-row';
    rowEl.dataset.row = String(r);
    if (this.deleteRows.has(r)) rowEl.classList.add('deleted');
    if (this.insertRows.has(r)) rowEl.classList.add('newrow');
    const row = this.rows[r];
    const edits = this.cellEdits.get(r);
    const touched = this.insertTouched.get(r);
    for (let c = 0; c < this.columns.length; c++) {
      const cell = document.createElement('div');
      cell.className = 'wdb-grid-cell';
      cell.dataset.row = String(r);
      cell.dataset.col = String(c);
      // Несохранённое значение (если ячейка изменена) поверх исходного.
      const hasEdit = !!(edits && edits.has(c));
      const v = hasEdit ? edits.get(c) : row[c];
      if (v === null || v === undefined) {
        // NULL — серым курсивом.
        const span = document.createElement('span');
        span.className = 'wdb-null';
        span.textContent = 'NULL';
        cell.appendChild(span);
      } else {
        cell.textContent = String(v);
        cell.title = String(v);
      }
      if (hasEdit || (touched && touched.has(c))) cell.classList.add('dirty');
      if (this.isSelected(r, c)) cell.classList.add('selected');
      rowEl.appendChild(cell);
    }
    return rowEl;
  }

  // ---------------- Индикация ожидания ----------------

  /// Показывает ожидание одним из двух способов:
  /// 'page' — скелет вместо тела (строк ещё нет: первая страница, сортировка,
  ///          новый запрос), 'more' — бегунок в статусбаре (строки уже есть,
  ///          ждём продолжение keyset-страницы), null — снять индикацию.
  setLoading(kind) {
    this.skeleton.hidden = kind !== 'page';
    this.status.classList.toggle('loading', kind === 'more');
  }

  // ---------------- Статусбар ----------------

  setStatus(text, isError) {
    this.status.innerHTML = '';
    const span = document.createElement('span');
    if (isError) span.className = 'error';
    span.textContent = text;
    this.status.appendChild(span);
  }

  updateStatus(extra) {
    const parts = [`Строк: ${this.rows.length}${this.truncated ? ' (результат обрезан)' : ''}`];
    if (this.elapsedMs !== null) parts.push(`Время: ${this.elapsedMs} мс`);
    if (this.mode === 'table' && this.nextAfter) parts.push('Прокрутите вниз для загрузки следующих строк');
    if (extra) parts.push(extra);
    this.status.innerHTML = '';
    for (const p of parts) {
      const span = document.createElement('span');
      span.textContent = p;
      this.status.appendChild(span);
    }
  }

  // ---------------- Выделение и копирование ----------------

  onCellMouseDown(e) {
    const cell = e.target.closest('.wdb-grid-cell');
    if (!cell) return;
    const r = parseInt(cell.dataset.row, 10);
    const c = parseInt(cell.dataset.col, 10);
    if (e.shiftKey && this.selAnchor) {
      this.selFocus = { r, c };
    } else {
      this.selAnchor = { r, c };
      this.selFocus = { r, c };
    }
    this.viewport.focus();
    this.scheduleRender();
  }

  isSelected(r, c) {
    if (!this.selAnchor || !this.selFocus) return false;
    const r1 = Math.min(this.selAnchor.r, this.selFocus.r);
    const r2 = Math.max(this.selAnchor.r, this.selFocus.r);
    const c1 = Math.min(this.selAnchor.c, this.selFocus.c);
    const c2 = Math.max(this.selAnchor.c, this.selFocus.c);
    return r >= r1 && r <= r2 && c >= c1 && c <= c2;
  }

  copySelection() {
    if (!this.selAnchor || !this.selFocus) return;
    const r1 = Math.min(this.selAnchor.r, this.selFocus.r);
    const r2 = Math.max(this.selAnchor.r, this.selFocus.r);
    const c1 = Math.min(this.selAnchor.c, this.selFocus.c);
    const c2 = Math.max(this.selAnchor.c, this.selFocus.c);
    const lines = [];
    for (let r = r1; r <= r2; r++) {
      const vals = [];
      for (let c = c1; c <= c2; c++) {
        const v = this.rows[r] ? this.rows[r][c] : null;
        vals.push(v === null || v === undefined ? '' : String(v));
      }
      lines.push(vals.join('\t')); // TSV
    }
    const text = lines.join('\n');
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).catch(() => {});
    } else {
      const ta = document.createElement('textarea');
      ta.value = text;
      document.body.appendChild(ta);
      ta.select();
      try { document.execCommand('copy'); } catch (_) { /* ignore */ }
      ta.remove();
    }
  }

  // ---------------- Режим query: SSE-стрим результатов ----------------

  connectStream(executionId) {
    this.closeStream();
    this.reset();
    this.currentExecutionId = executionId;
    this.setStatus('Выполняется…');
    // До первой строки показывать нечего: место результата держит скелет.
    this.setLoading('page');
    const es = new EventSource(`/api/query/stream/${executionId}`);
    this.eventSource = es;

    es.addEventListener('meta', (e) => {
      const d = JSON.parse(e.data);
      // Каждый новый resultset (мульти-statement скрипт) начинает грид заново.
      const keepStatus = this.status.textContent;
      this.rows = [];
      this.spacer.style.height = '0px';
      this.canvas.innerHTML = '';
      this.setColumns(d.columns);
      this.setStatus(keepStatus || 'Выполняется…');
    });
    es.addEventListener('row', (e) => {
      const d = JSON.parse(e.data);
      this.appendRows(d.rows);
    });
    es.addEventListener('done', (e) => {
      const d = JSON.parse(e.data);
      this.truncated = !!d.truncated;
      this.elapsedMs = d.elapsedMs;
      this.closeStream();
      if (this.columns.length === 0 && d.affectedRows !== null && d.affectedRows !== undefined) {
        this.setStatus(`Затронуто строк: ${d.affectedRows} • Время: ${d.elapsedMs} мс`);
      } else {
        this.updateStatus();
      }
    });
    es.addEventListener('error', (e) => {
      // Кастомное событие error (с данными) или обрыв соединения (без данных).
      if (e.data) {
        const d = JSON.parse(e.data);
        this.setStatus('Ошибка: ' + d.message, true);
        toast(d.message);
      } else if (es.readyState === EventSource.CLOSED) {
        this.setStatus('Соединение со стримом прервано.', true);
      }
      this.closeStream();
    });
  }

  closeStream() {
    if (this.eventSource) {
      this.eventSource.close();
      this.eventSource = null;
      // Стрим закончился любым исходом (done, ошибка, отмена) — ожидание снято.
      this.setLoading(null);
      // Редактор снимает состояние «выполняется» (кнопка «Отмена», статусбар).
      document.dispatchEvent(new CustomEvent('webdb:query-finished', {
        detail: { executionId: this.currentExecutionId },
      }));
    }
  }

  async cancel() {
    if (!this.currentExecutionId) return;
    try {
      await fetch(`/api/query/cancel/${this.currentExecutionId}`, { method: 'POST' });
    } catch (_) { /* ignore */ }
  }

  // ---------------- Режим table: keyset-страницы ----------------

  pageUrl(after) {
    const p = new URLSearchParams({
      dsId: this.el.dataset.dsId,
      schema: this.el.dataset.schema,
      table: this.el.dataset.table,
    });
    if (this.el.dataset.database) p.set('db', this.el.dataset.database);
    if (this.el.dataset.limit) p.set('limit', this.el.dataset.limit);
    if (this.orderBy) p.set('orderBy', this.orderBy);
    if (this.desc) p.set('desc', 'true');
    if (this.el.dataset.filter) p.set('filter', this.el.dataset.filter);
    if (after) p.set('after', JSON.stringify(after));
    return '/api/data/page?' + p.toString();
  }

  async loadFirstPage() {
    this.reset();
    await this.loadPage(null);
  }

  async loadPage(after) {
    if (this.loading) return;
    this.loading = true;
    // Первая страница ждёт «вместо содержимого» (скелет), следующая —
    // «в дополнение к содержимому» (бегунок в статусбаре).
    this.setLoading(after ? 'more' : 'page');
    // Перечитывание таблицы (открытие страницы, фильтр, сортировка) блокирует ввод
    // целиком: тулбар остался бы кликабельным, а нажатия в нём относились бы к
    // выборке, которая уже отменена. Отменить чтение страницы всё равно нечем.
    // Догрузка при прокрутке не блокирует — работать с уже показанными строками можно.
    const blockScreen = !after && window.WebDb && typeof window.WebDb.blockScreen === 'function';
    if (blockScreen) window.WebDb.blockScreen();
    // Статус переписывается только при перечитывании таблицы: у догрузки он
    // остаётся прежним, иначе сообщение о неудаче предыдущей страницы
    // затиралось бы каждой попыткой прокрутки.
    if (!after) this.setStatus('Загрузка…');
    const started = performance.now();
    try {
      const res = await fetch(this.pageUrl(after));
      if (!res.ok) {
        let msg = 'Ошибка загрузки данных таблицы.';
        try { const err = await res.json(); if (err && err.error) msg = err.error; } catch (_) { /* не JSON */ }
        this.setStatus(msg, true);
        toast(msg);
        return;
      }
      const d = await res.json();
      if (this.columns.length === 0) this.setColumns(d.columns);
      this.nextAfter = d.nextAfter || null;
      this.elapsedMs = Math.round(performance.now() - started);
      this.appendRows(d.rows);
      this.updateStatus();
    } catch (e) {
      this.setStatus('Сеть недоступна.', true);
    } finally {
      this.loading = false;
      this.setLoading(null);
      if (blockScreen) window.WebDb.unblockScreen();
    }
  }

  maybeLoadMore() {
    // Бесконечная прокрутка: подгрузка следующей keyset-страницы у нижней границы.
    if (!this.nextAfter || this.loading) return;
    const nearBottom = this.viewport.scrollTop + this.viewport.clientHeight
      >= this.rows.length * ROW_HEIGHT - LOAD_THRESHOLD;
    if (nearBottom) this.loadPage(this.nextAfter);
  }

  sortBy(columnName) {
    if (this.orderBy === columnName) this.desc = !this.desc;
    else { this.orderBy = columnName; this.desc = false; }
    this.loadFirstPage();
  }

  // ---------------- Inline-редактирование (режим table) ----------------

  /// Панель «Сохранить (N)»/«Отменить»/«+ Строка»/«Удалить строки».
  /// Ищем партиал [data-edit-panel] во внешних контейнерах, иначе создаём панель сами.
  initEditPanel() {
    let panel = null;
    for (let node = this.el.parentElement; node && !panel; node = node.parentElement)
      panel = node.querySelector('[data-edit-panel]');
    if (!panel) {
      panel = document.createElement('div');
      panel.className = 'wdb-edit-panel';
      panel.setAttribute('data-edit-panel', '');
      panel.hidden = true;
      // Разметка повторяет партиал DataEditPanel.cshtml: иконка плюс aria-label
      // и data-tip. Счётчик изменений остаётся видимым — это данные, не подпись.
      panel.innerHTML =
        `<button type="button" class="btn btn-primary btn-icon btn-icon-count" data-edit-action="save"
                 aria-label="Сохранить изменения" data-tip="Отправить накопленные изменения в базу данных"
                 >${ICONS.save}<span class="btn-count" data-edit-count>0</span></button>` +
        `<button type="button" class="btn btn-icon" data-edit-action="discard"
                 aria-label="Отменить изменения" data-tip="Отбросить все несохранённые правки"
                 >${ICONS.discard}</button>` +
        `<button type="button" class="btn btn-icon" data-edit-action="add-row"
                 aria-label="Добавить строку" data-tip="Добавить пустую строку в конец таблицы"
                 >${ICONS.addRow}</button>` +
        `<button type="button" class="btn btn-icon" data-edit-action="delete-rows"
                 aria-label="Удалить выделенные строки" data-tip="Пометить выделенные строки на удаление"
                 >${ICONS.deleteRows}</button>`;
      this.el.appendChild(panel);
    }
    this.editPanel = panel;
    if (panel.dataset.readonly === 'true') this.editable = false;
    panel.addEventListener('click', (e) => {
      const btn = e.target.closest('[data-edit-action]');
      if (!btn || btn.disabled || btn.getAttribute('aria-disabled') === 'true') return;
      switch (btn.dataset.editAction) {
        case 'save': this.saveEdits(); break;
        case 'discard': this.discardEdits(); break;
        case 'add-row': this.addNewRow(); break;
        case 'delete-rows': this.markSelectedRowsDeleted(); break;
      }
    });
    if (this.editable) this.loadTableMeta();
    this.updateEditPanel();
  }

  /// Метаданные таблицы для редактора: PK, псевдоколонка адреса строки, свойства колонок.
  async loadTableMeta() {
    try {
      const p = new URLSearchParams({
        dsId: this.el.dataset.dsId,
        schema: this.el.dataset.schema,
        table: this.el.dataset.table,
      });
      if (this.el.dataset.database) p.set('db', this.el.dataset.database);
      const res = await fetch('/api/data/edit/table-info?' + p.toString());
      if (!res.ok) { this.editable = false; this.updateEditPanel(); return; }
      this.tableMeta = await res.json();
      if (this.tableMeta.readOnly) this.editable = false;
    } catch (_) {
      this.editable = false;
    }
    this.updateEditPanel();
  }

  /// Ключевые колонки идентификации строки: PK либо псевдоколонка ctid/__ROWID.
  keyColumnNames() {
    const m = this.tableMeta;
    if (!m) return null;
    if (m.primaryKey && m.primaryKey.length) return m.primaryKey;
    return m.rowAddressColumn ? [m.rowAddressColumn] : null;
  }

  colIndexByName(name) {
    return this.columns.findIndex((col) => col.name === name);
  }

  /// Исходные (несохранённые правки не учитываются) значения ключевых колонок строки.
  rowKeyValues(r, keys) {
    const result = {};
    for (const name of keys) {
      const c = this.colIndexByName(name);
      result[name] = c >= 0 ? this.rows[r][c] : null;
    }
    return result;
  }

  /// Текущее значение ячейки с учётом несохранённой правки.
  cellValue(r, c) {
    const edits = this.cellEdits.get(r);
    return edits && edits.has(c) ? edits.get(c) : this.rows[r][c];
  }

  /// Количество несохранённых операций (изменённые + новые + помеченные на удаление строки).
  pendingCount() {
    let n = 0;
    for (const r of this.cellEdits.keys()) if (!this.deleteRows.has(r)) n++;
    for (const r of this.insertRows) if (!this.deleteRows.has(r)) n++;
    for (const r of this.deleteRows) if (!this.insertRows.has(r)) n++;
    return n;
  }

  updateEditPanel() {
    const p = this.editPanel;
    if (!p) return;
    p.hidden = !this.editable;
    const count = this.pendingCount();
    const cnt = p.querySelector('[data-edit-count]');
    if (cnt) cnt.textContent = String(count);
    // aria-disabled вместо disabled: кнопки панели иконочные и в неактивном
    // состоянии обязаны оставаться наводимыми, иначе подсказка недостижима.
    const toggle = (action, disabled) => {
      const b = p.querySelector(`[data-edit-action="${action}"]`);
      if (b) b.setAttribute('aria-disabled', String(disabled));
    };
    toggle('save', this.saving || count === 0);
    toggle('discard', this.saving || count === 0);
    toggle('add-row', this.saving || !this.tableMeta);
    toggle('delete-rows', this.saving || !this.tableMeta);
  }

  /// Двойной клик/Enter по ячейке — инлайн-редактор (textarea-попап для многострочных значений).
  beginEdit(r, c) {
    if (!this.editable || this.activeEditor || this.saving) return;
    if (!Number.isInteger(r) || !Number.isInteger(c)) return;
    if (r < 0 || r >= this.rows.length || c < 0 || c >= this.columns.length) return;
    if (this.deleteRows.has(r)) return; // строка помечена на удаление
    const meta = this.tableMeta;
    if (!meta) { toast('Метаданные таблицы ещё загружаются — попробуйте ещё раз.', 'info'); return; }
    const colName = this.columns[c].name;
    if (meta.rowAddressColumn && colName === meta.rowAddressColumn) return; // ctid/__ROWID не редактируется
    const colMeta = meta.columns.find((x) => x.name === colName);
    if (colMeta && colMeta.isGenerated) {
      toast(`Колонка «${colName}» — генерируемая, её значение изменить нельзя.`, 'info');
      return;
    }

    const current = this.cellValue(r, c);
    const text = current === null || current === undefined ? '' : String(current);
    if (text.includes('\n') || text.length > 160) {
      this.openPopupEditor(r, c, current, colMeta);
      return;
    }

    const cellEl = this.canvas.querySelector(`.wdb-grid-cell[data-row="${r}"][data-col="${c}"]`);
    if (!cellEl) return;

    const wrap = document.createElement('div');
    wrap.className = 'wdb-cell-editor';
    const input = document.createElement('input');
    input.type = 'text';
    input.value = text;
    input.disabled = current === null || current === undefined;
    wrap.appendChild(input);

    // NULL-чекбокс (только для nullable-колонок).
    let nullCheck = null;
    if (!colMeta || colMeta.isNullable) {
      const label = document.createElement('label');
      nullCheck = document.createElement('input');
      nullCheck.type = 'checkbox';
      nullCheck.checked = current === null || current === undefined;
      label.append(nullCheck, document.createTextNode('NULL'));
      label.title = 'Записать NULL вместо значения';
      wrap.appendChild(label);
      nullCheck.addEventListener('change', () => {
        input.disabled = nullCheck.checked;
        if (!nullCheck.checked) input.focus();
      });
    } else {
      input.disabled = false;
    }

    cellEl.classList.remove('selected');
    cellEl.replaceChildren(wrap);

    const finish = (commit) => {
      if (!this.activeEditor) return;
      this.activeEditor = null;
      if (commit) {
        const value = nullCheck && nullCheck.checked ? null : input.value;
        this.applyCellEdit(r, c, value);
      }
      this.scheduleRender();
      this.viewport.focus();
    };
    input.addEventListener('keydown', (e) => {
      e.stopPropagation(); // не отдаём Enter/Esc гриду
      if (e.key === 'Enter') { finish(true); e.preventDefault(); }
      else if (e.key === 'Escape') { finish(false); e.preventDefault(); }
    });
    if (nullCheck) nullCheck.addEventListener('keydown', (e) => {
      e.stopPropagation();
      if (e.key === 'Enter') { finish(true); e.preventDefault(); }
      else if (e.key === 'Escape') { finish(false); e.preventDefault(); }
    });
    wrap.addEventListener('focusout', (e) => {
      // Клик вне редактора — фиксация значения.
      if (!wrap.contains(e.relatedTarget)) finish(true);
    });

    this.activeEditor = { commit: () => finish(true), cancel: () => finish(false) };
    if (!input.disabled) { input.focus(); input.select(); } else if (nullCheck) nullCheck.focus();
  }

  /// Попап «Редактор ячейки» для многострочных/длинных значений.
  openPopupEditor(r, c, current, colMeta) {
    const overlay = document.createElement('div');
    overlay.className = 'wdb-popup-overlay';
    const popup = document.createElement('div');
    popup.className = 'wdb-popup';

    const title = document.createElement('div');
    title.className = 'wdb-popup-title';
    title.textContent = `Редактор ячейки — ${this.columns[c].name}`;

    const ta = document.createElement('textarea');
    ta.value = current === null || current === undefined ? '' : String(current);
    ta.disabled = current === null || current === undefined;

    const footer = document.createElement('div');
    footer.className = 'wdb-popup-footer';
    let nullCheck = null;
    if (!colMeta || colMeta.isNullable) {
      const label = document.createElement('label');
      nullCheck = document.createElement('input');
      nullCheck.type = 'checkbox';
      nullCheck.checked = current === null || current === undefined;
      label.append(nullCheck, document.createTextNode(' NULL'));
      footer.appendChild(label);
      nullCheck.addEventListener('change', () => { ta.disabled = nullCheck.checked; });
    } else {
      ta.disabled = false;
    }
    const spacer = document.createElement('span');
    spacer.className = 'spacer';
    const okBtn = document.createElement('button');
    okBtn.type = 'button';
    okBtn.className = 'btn btn-primary btn-icon';
    okBtn.setAttribute('aria-label', 'Применить значение');
    okBtn.setAttribute('data-tip', 'Применить значение к ячейке');
    okBtn.innerHTML = ICONS.confirm;
    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-icon';
    cancelBtn.setAttribute('aria-label', 'Отмена');
    cancelBtn.setAttribute('data-tip', 'Закрыть, не изменяя ячейку');
    cancelBtn.innerHTML = ICONS.close;
    footer.append(spacer, okBtn, cancelBtn);

    popup.append(title, ta, footer);
    overlay.appendChild(popup);
    document.body.appendChild(overlay);

    const finish = (commit) => {
      if (!this.activeEditor) return;
      this.activeEditor = null;
      overlay.remove();
      if (commit) {
        const value = nullCheck && nullCheck.checked ? null : ta.value;
        this.applyCellEdit(r, c, value);
      }
      this.scheduleRender();
      this.viewport.focus();
    };
    okBtn.addEventListener('click', () => finish(true));
    cancelBtn.addEventListener('click', () => finish(false));
    overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) finish(false); });
    overlay.addEventListener('keydown', (e) => {
      e.stopPropagation();
      if (e.key === 'Escape') { finish(false); e.preventDefault(); }
      else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { finish(true); e.preventDefault(); }
    });

    this.activeEditor = { commit: () => finish(true), cancel: () => finish(false) };
    if (!ta.disabled) ta.focus(); else if (nullCheck) nullCheck.focus();
  }

  /// Фиксация правки ячейки: для новой строки — прямо в значения, иначе — в карту изменений.
  applyCellEdit(r, c, value) {
    if (this.insertRows.has(r)) {
      this.rows[r][c] = value;
      let t = this.insertTouched.get(r);
      if (!t) this.insertTouched.set(r, t = new Set());
      t.add(c);
    } else {
      const original = this.rows[r][c];
      const wasNull = original === null || original === undefined;
      const same = value === null ? wasNull : (!wasNull && String(original) === value);
      let m = this.cellEdits.get(r);
      if (same) {
        // Значение вернулось к исходному — правка снимается.
        if (m) { m.delete(c); if (!m.size) this.cellEdits.delete(r); }
      } else {
        if (!m) this.cellEdits.set(r, m = new Map());
        m.set(c, value);
      }
    }
    this.updateEditPanel();
  }

  /// «+ Строка»: добавляет пустую (NULL) строку в конец грида.
  addNewRow() {
    if (!this.editable || !this.tableMeta || this.saving) return;
    const r = this.rows.length;
    this.rows.push(new Array(this.columns.length).fill(null));
    this.insertRows.add(r);
    this.insertTouched.set(r, new Set());
    this.spacer.style.height = (this.rows.length * ROW_HEIGHT) + 'px';
    this.selAnchor = this.selFocus = { r, c: 0 };
    this.viewport.scrollTop = this.rows.length * ROW_HEIGHT;
    this.scheduleRender();
    this.updateEditPanel();
    this.updateStatus();
  }

  /// «Удалить строки»: помечает выделенные строки на удаление (с подтверждением).
  markSelectedRowsDeleted() {
    if (!this.editable || this.saving) return;
    if (!this.selAnchor || !this.selFocus) {
      toast('Сначала выделите строки, которые нужно удалить.', 'info');
      return;
    }
    const r1 = Math.min(this.selAnchor.r, this.selFocus.r);
    const r2 = Math.max(this.selAnchor.r, this.selFocus.r);
    const n = r2 - r1 + 1;
    if (!window.confirm(`Пометить на удаление строк: ${n}? Удаление в базе произойдёт при сохранении.`)) return;
    for (let r = r1; r <= r2; r++) {
      this.deleteRows.add(r);
      this.cellEdits.delete(r); // правки удаляемой строки не имеют смысла
    }
    this.scheduleRender();
    this.updateEditPanel();
  }

  /// «Отменить»: сброс всех несохранённых изменений.
  discardEdits() {
    if (this.saving) return;
    if (this.activeEditor) this.activeEditor.cancel();
    if (this.insertRows.size) this.rows = this.rows.filter((_, i) => !this.insertRows.has(i));
    this.cellEdits.clear();
    this.insertRows.clear();
    this.insertTouched.clear();
    this.deleteRows.clear();
    this.selAnchor = this.selFocus = null;
    this.spacer.style.height = (this.rows.length * ROW_HEIGHT) + 'px';
    this.scheduleRender();
    this.updateEditPanel();
    this.updateStatus();
  }

  /// «Сохранить (N)»: пакет изменений → POST /api/data/edit; ошибки — по-строчно (тосты).
  async saveEdits() {
    if (this.saving || !this.editable || !this.tableMeta) return;
    if (this.activeEditor) this.activeEditor.commit();
    const keys = this.keyColumnNames();
    const schema = this.el.dataset.schema;
    const table = this.el.dataset.table;
    const edits = [];
    const editRowIndex = []; // индекс изменения → индекс строки грида (для сообщений об ошибках)

    // UPDATE — изменённые существующие строки.
    for (const [r, m] of this.cellEdits) {
      if (this.deleteRows.has(r) || this.insertRows.has(r)) continue;
      if (!keys) { toast('У таблицы нет первичного ключа и адресной псевдоколонки — редактирование невозможно.'); return; }
      const changed = {};
      for (const [c, v] of m) changed[this.columns[c].name] = v;
      edits.push({ schema, table, kind: 'update', keyValues: this.rowKeyValues(r, keys), changedValues: changed });
      editRowIndex.push(r);
    }
    // DELETE — помеченные строки (кроме несохранённых новых).
    for (const r of [...this.deleteRows].sort((a, b) => a - b)) {
      if (this.insertRows.has(r)) continue;
      if (!keys) { toast('У таблицы нет первичного ключа и адресной псевдоколонки — удаление невозможно.'); return; }
      edits.push({ schema, table, kind: 'delete', keyValues: this.rowKeyValues(r, keys), changedValues: null });
      editRowIndex.push(r);
    }
    // INSERT — новые строки (заполненные колонки; пусто = все DEFAULT).
    for (const r of [...this.insertRows].sort((a, b) => a - b)) {
      if (this.deleteRows.has(r)) continue;
      const t = this.insertTouched.get(r) || new Set();
      const changed = {};
      for (const c of t) changed[this.columns[c].name] = this.rows[r][c];
      edits.push({ schema, table, kind: 'insert', keyValues: null, changedValues: changed });
      editRowIndex.push(r);
    }
    if (!edits.length) { toast('Нет изменений для сохранения.', 'info'); return; }

    this.saving = true;
    this.updateEditPanel();
    try {
      const res = await fetch('/api/data/edit', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ dsId: this.el.dataset.dsId, db: this.el.dataset.database || null, edits }),
      });
      let d = null;
      try { d = await res.json(); } catch (_) { /* не JSON */ }
      if (!res.ok) {
        toast((d && d.error) || `Ошибка сохранения (HTTP ${res.status}).`);
        return;
      }
      const failures = (d.results || []).filter((x) => !x.success);
      if (failures.length === 0) {
        toast(`Сохранено изменений: ${d.applied}.`, 'info');
        if (d.inTransaction && !d.committed)
          toast('Изменения применены в открытой транзакции — не забудьте выполнить COMMIT.', 'info');
        this.loadFirstPage(); // reset() очистит состояние правок и перечитает данные
      } else {
        for (const f of failures.slice(0, 5)) {
          const r = editRowIndex[f.index];
          toast(`Строка ${r !== undefined ? r + 1 : f.index + 1}: ${f.error}`);
        }
        if (failures.length > 5) toast(`…и ещё ошибок: ${failures.length - 5}.`);
        if (d.rolledBack) toast('Все изменения пакета отменены (откат транзакции).', 'info');
      }
    } catch (_) {
      toast('Сеть недоступна — изменения не сохранены.');
    } finally {
      this.saving = false;
      this.updateEditPanel();
    }
  }
}

// ---------------- Инициализация и связка с редактором ----------------

const grids = new WeakMap();

function initGrid(el) {
  if (grids.has(el)) return grids.get(el);
  injectCss();
  const grid = new VirtualGrid(el);
  grids.set(el, grid);
  return grid;
}

function initAll(root) {
  const scope = root && root.querySelectorAll ? root : document;
  scope.querySelectorAll('[data-result-grid]').forEach(initGrid);
}

document.addEventListener('DOMContentLoaded', () => initAll(document));
document.addEventListener('htmx:load', (e) => initAll(e.target || document));
document.addEventListener('htmx:afterSwap', (e) => initAll(e.target || document));

// Запуск стрима результатов после /api/query/execute (событие из editor.js).
document.addEventListener('webdb:execute', (e) => {
  const { executionId, gridTarget } = e.detail || {};
  if (!executionId) return;
  let el = gridTarget ? document.querySelector(gridTarget) : null;
  if (!el) el = document.querySelector('[data-result-grid]:not([data-mode="table"])');
  if (!el) el = document.querySelector('[data-result-grid]');
  if (!el) { toast('На странице нет грида результатов.'); return; }
  initGrid(el).connectStream(executionId);
});

window.WebDbGrid = { initAll, initGrid, get: (el) => grids.get(el) };
