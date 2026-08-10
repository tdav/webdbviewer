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
  generate: `<svg ${ICON_ATTRS}><path d="M13.4 3.4v3.4H10"/><path d="M12.7 6.6a5.2 5.2 0 1 0-.5 5.2"/></svg>`,
  calendar: `<svg ${ICON_ATTRS}><rect x="2.4" y="3.6" width="11.2" height="10" rx="1.2"/><path d="M2.4 6.8h11.2M5.6 2.4v2.4M10.4 2.4v2.4"/></svg>`,
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
/* Попап живёт в body, а не внутри .wdb-grid — токены --wdb-* там не видны,
   и объявлять их приходится заново. Без этого var(--wdb-bg) не разрешается:
   фон окна становится прозрачным, а рамки полей пропадают целиком. */
.wdb-popup-overlay{
  --wdb-bg:var(--bg-panel,#fff);
  --wdb-fg:var(--text,#1a1a24);
  --wdb-border:var(--border,rgba(26,26,46,.12));
  --wdb-null:var(--text-muted,#606070);
  --wdb-mono:var(--mono,ui-monospace,Consolas,monospace);
  position:fixed;inset:0;background:rgba(10,10,14,.58);z-index:var(--z-modal,50);
  display:flex;align-items:center;justify-content:center
}
.wdb-popup{background:var(--wdb-bg);color:var(--wdb-fg);border:1px solid var(--border-strong,rgba(26,26,46,.22));border-radius:var(--radius-xl,12px);box-shadow:var(--shadow-overlay,0 8px 24px rgba(0,0,0,.45));width:560px;max-width:92vw;max-height:88vh;display:flex;flex-direction:column;font:13px var(--font-ui,system-ui,sans-serif)}
.wdb-popup-title{padding:8px 12px;font-weight:600;border-bottom:1px solid var(--wdb-border)}
.wdb-popup textarea{margin:10px 12px 4px;min-height:180px;resize:vertical;font:13px var(--wdb-mono);background:var(--bg-well,#e9e9f1);color:var(--wdb-fg);border:1px solid var(--wdb-border);border-radius:var(--radius-md,6px);padding:6px 8px}
.wdb-popup-footer{display:flex;align-items:center;gap:8px;padding:8px 12px}
.wdb-popup-footer .spacer{margin-left:auto}
/* Форма строки (вставка/правка результата запроса): имя — значение — NULL. */
/* Колонки: имя — значение — помощник (UUID/календарь) — NULL. Помощник вынесен
   в свою колонку: рядом с полем он читался бы как часть значения. */
.wdb-popup-form{display:grid;grid-template-columns:minmax(110px,auto) minmax(0,1fr) auto auto;gap:6px 10px;align-items:center;margin:10px 12px 4px;max-height:56vh;overflow:auto}
.wdb-popup-form .name{font-weight:600;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.wdb-popup-form .name .type{display:block;font-weight:400;font-size:11px;color:var(--wdb-null)}
.wdb-popup-form input[type=text]{width:100%;box-sizing:border-box;font:13px var(--wdb-mono);padding:3px 6px;background:var(--bg-well,#e9e9f1);color:var(--wdb-fg);border:1px solid var(--wdb-border);border-radius:var(--radius-md,6px)}
.wdb-popup-form input[type=text]:disabled{opacity:.55}
.wdb-popup-form .helper{display:flex;align-items:center}

/* Календарь с выбором времени. Живёт в body поверх модального окна, поэтому
   берёт токены приложения напрямую — токены грида здесь не видны. */
.wdb-dtp-backdrop{position:fixed;inset:0;z-index:calc(var(--z-modal,50) + 10)}
.wdb-dtp{
  position:fixed;display:flex;overflow:hidden;
  background:var(--bg-panel,#fff);color:var(--text,#1a1a24);
  border:1px solid var(--border-strong,rgba(26,26,46,.22));
  border-radius:var(--radius-xl,12px);box-shadow:var(--shadow-overlay,0 8px 24px rgba(0,0,0,.45));
  font:13px var(--font-ui,system-ui,sans-serif)
}
.wdb-dtp-cal{padding:10px 12px}
.wdb-dtp-head{display:flex;align-items:center;gap:4px;margin-bottom:6px}
.wdb-dtp-title{flex:1;text-align:center;font-weight:600}
.wdb-dtp-nav{width:26px;height:26px;display:inline-flex;align-items:center;justify-content:center;border:0;background:transparent;color:var(--text-muted,#606070);border-radius:var(--radius-md,6px);cursor:pointer;font:15px/1 var(--font-ui,system-ui,sans-serif)}
.wdb-dtp-nav:hover{background:var(--bg-hover,#e4e4ec);color:var(--text,#1a1a24)}
.wdb-dtp-grid{display:grid;grid-template-columns:repeat(7,30px);gap:2px}
.wdb-dtp-wd{height:22px;display:flex;align-items:center;justify-content:center;font-size:11px;color:var(--text-muted,#606070)}
.wdb-dtp-day{height:28px;border:0;background:transparent;color:var(--text,#1a1a24);border-radius:var(--radius-md,6px);cursor:pointer;font:13px var(--font-ui,system-ui,sans-serif)}
.wdb-dtp-day:hover{background:var(--bg-hover,#e4e4ec)}
/* Дни соседних месяцев остаются кликабельными, но не спорят за внимание с текущим. */
.wdb-dtp-day.out{color:var(--text-muted,#606070);opacity:.6}
.wdb-dtp-day.today{box-shadow:inset 0 0 0 1px var(--border-strong,rgba(26,26,46,.22))}
.wdb-dtp-day.sel{background:var(--accent,#ff6b2c);color:var(--accent-ink,#fff);font-weight:600}
.wdb-dtp-foot{display:flex;margin-top:8px;padding-top:8px;border-top:1px solid var(--border,rgba(26,26,46,.12))}
.wdb-dtp-link{border:0;background:transparent;color:var(--accent-text,#c25100);cursor:pointer;font:12px var(--font-ui,system-ui,sans-serif);padding:2px 6px;border-radius:var(--radius-md,6px)}
.wdb-dtp-link:hover{background:var(--accent-soft,rgba(255,107,44,.12))}
.wdb-dtp-times{width:92px;max-height:294px;overflow-y:auto;display:flex;flex-direction:column;gap:4px;padding:10px 8px;border-left:1px solid var(--border,rgba(26,26,46,.12));background:var(--bg-panel-alt,#f0f0f4)}
.wdb-dtp-time{flex:none;padding:5px 0;border:0;border-radius:var(--radius-md,6px);background:var(--bg-well,#e9e9f1);color:var(--text,#1a1a24);cursor:pointer;font:12px var(--mono,ui-monospace,Consolas,monospace)}
.wdb-dtp-time:hover{background:var(--bg-hover,#e4e4ec)}
.wdb-dtp-time.sel{background:var(--accent,#ff6b2c);color:var(--accent-ink,#fff)}
.wdb-popup-form .nullbox{display:flex;align-items:center;gap:3px;font-size:11px;white-space:nowrap;cursor:pointer;user-select:none}
.wdb-popup-hint{padding:0 12px;font-size:11px;color:var(--wdb-null)}
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

// --- Помощники поля формы строки ---

function helperButton(icon, tip) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = 'btn btn-sm btn-icon';
  b.setAttribute('aria-label', tip);
  b.setAttribute('data-tip', tip);
  b.innerHTML = icon;
  return b;
}

/// UUID v4. crypto.randomUUID есть только в защищённом контексте (https или
/// localhost), а приложение разворачивают и по http — там остаётся getRandomValues.
function newUuid() {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6] & 0x0f) | 0x40; // версия 4
  b[8] = (b[8] & 0x3f) | 0x80; // вариант RFC 4122
  const hex = [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

// --- Календарь с выбором времени ---

const MONTHS = ['Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь',
  'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'];
const WEEKDAYS = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
const TIME_STEP_MINUTES = 30;

function pad(value, length = 2) {
  return String(value).padStart(length, '0');
}

/// Date → «2023-10-09 10:59:59.000» (без времени — «2023-10-09»).
function formatDateTime(d, withTime) {
  const date = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
  if (!withTime) return date;
  return `${date} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

/// Разбор значения поля. null — текст не распознан: календарь откроется на сегодня.
function parseDateTime(text) {
  const m = /^(\d{4})-(\d{1,2})-(\d{1,2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.(\d{1,3}))?)?/
    .exec(String(text || '').trim());
  if (!m) return null;
  const d = new Date(+m[1], +m[2] - 1, +m[3], +(m[4] || 0), +(m[5] || 0), +(m[6] || 0),
    +(m[7] || '0').padEnd(3, '0'));
  return Number.isNaN(d.getTime()) ? null : d;
}

function sameDay(a, b) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

/**
 * Календарь с колонкой времени, привязанный к кнопке поля.
 * Нативный datetime-local выглядит по-своему в каждом браузере и списка времени
 * не даёт — поэтому свой, на токенах приложения.
 * @param {HTMLElement} anchor кнопка, у которой раскрывается панель
 * @param {string} currentText текущее значение поля
 * @param {boolean} withTime показывать колонку времени
 * @param {(value:string)=>void} onPick выбранное значение в формате поля
 */
function openDateTimePicker(anchor, currentText, withTime, onPick) {
  const backdrop = document.createElement('div');
  backdrop.className = 'wdb-dtp-backdrop';
  const panel = document.createElement('div');
  panel.className = 'wdb-dtp';
  backdrop.appendChild(panel);

  const today = new Date();
  let sel = parseDateTime(currentText) || today;
  let viewYear = sel.getFullYear();
  let viewMonth = sel.getMonth();

  const onKey = (e) => {
    if (e.key !== 'Escape') return;
    // Капчурим на документе: иначе Esc дошёл бы до формы строки и закрыл её тоже.
    e.stopPropagation();
    e.preventDefault();
    close();
  };
  function close() {
    backdrop.remove();
    document.removeEventListener('keydown', onKey, true);
    anchor.focus();
  }
  const apply = (date) => { onPick(formatDateTime(date, withTime)); close(); };

  // --- Календарь ---
  const cal = document.createElement('div');
  cal.className = 'wdb-dtp-cal';

  const head = document.createElement('div');
  head.className = 'wdb-dtp-head';
  const prev = document.createElement('button');
  const next = document.createElement('button');
  for (const [btn, label, step] of [[prev, 'Предыдущий месяц', -1], [next, 'Следующий месяц', 1]]) {
    btn.type = 'button';
    btn.className = 'wdb-dtp-nav';
    btn.setAttribute('aria-label', label);
    btn.textContent = step < 0 ? '‹' : '›';
    btn.addEventListener('click', () => {
      viewMonth += step;
      if (viewMonth < 0) { viewMonth = 11; viewYear--; }
      else if (viewMonth > 11) { viewMonth = 0; viewYear++; }
      renderMonth();
    });
  }
  const title = document.createElement('div');
  title.className = 'wdb-dtp-title';
  head.append(prev, title, next);

  const grid = document.createElement('div');
  grid.className = 'wdb-dtp-grid';

  const foot = document.createElement('div');
  foot.className = 'wdb-dtp-foot';
  const nowBtn = document.createElement('button');
  nowBtn.type = 'button';
  nowBtn.className = 'wdb-dtp-link';
  nowBtn.textContent = withTime ? 'Сейчас' : 'Сегодня';
  nowBtn.addEventListener('click', () => apply(new Date()));
  foot.appendChild(nowBtn);

  cal.append(head, grid, foot);

  function renderMonth() {
    title.textContent = `${MONTHS[viewMonth]} ${viewYear}`;
    grid.replaceChildren();
    for (const wd of WEEKDAYS) {
      const cell = document.createElement('div');
      cell.className = 'wdb-dtp-wd';
      cell.textContent = wd;
      grid.appendChild(cell);
    }
    // Неделя начинается с понедельника: getDay() отдаёт воскресенье нулём.
    const first = new Date(viewYear, viewMonth, 1);
    const offset = (first.getDay() + 6) % 7;
    for (let i = 0; i < 42; i++) {
      const date = new Date(viewYear, viewMonth, i - offset + 1);
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'wdb-dtp-day';
      if (date.getMonth() !== viewMonth) btn.classList.add('out');
      if (sameDay(date, today)) btn.classList.add('today');
      if (sameDay(date, sel)) btn.classList.add('sel');
      btn.textContent = String(date.getDate());
      btn.addEventListener('click', () => {
        // Время остаётся прежним: смена дня не должна сбрасывать секунды значения.
        sel = new Date(date.getFullYear(), date.getMonth(), date.getDate(),
          sel.getHours(), sel.getMinutes(), sel.getSeconds(), sel.getMilliseconds());
        if (!withTime) { apply(sel); return; }
        viewYear = sel.getFullYear();
        viewMonth = sel.getMonth();
        renderMonth();
        markTime();
      });
      grid.appendChild(btn);
    }
  }

  // --- Колонка времени ---
  let times = null;
  const timeButtons = [];
  if (withTime) {
    times = document.createElement('div');
    times.className = 'wdb-dtp-times';
    for (let m = 0; m < 24 * 60; m += TIME_STEP_MINUTES) {
      const h = Math.floor(m / 60);
      const min = m % 60;
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'wdb-dtp-time';
      btn.textContent = `${pad(h)}:${pad(min)}`;
      btn.dataset.minutes = String(m);
      // Выбор времени — последнее действие: секунды и доли обнуляются, окно закрывается.
      btn.addEventListener('click', () => apply(
        new Date(sel.getFullYear(), sel.getMonth(), sel.getDate(), h, min, 0, 0)));
      timeButtons.push(btn);
      times.appendChild(btn);
    }
  }

  function markTime() {
    if (!times) return;
    const current = sel.getHours() * 60 + sel.getMinutes();
    for (const btn of timeButtons) btn.classList.toggle('sel', +btn.dataset.minutes === current);
  }

  renderMonth();
  markTime();
  panel.appendChild(cal);
  if (times) panel.appendChild(times);
  document.body.appendChild(backdrop);

  // Прокрутка списка к выбранному времени — своя, а не scrollIntoView:
  // тот дёргает и внешние прокручиваемые контейнеры.
  if (times) {
    const active = timeButtons.find((b) => b.classList.contains('sel'))
      || timeButtons[Math.floor(sel.getHours() * 60 / TIME_STEP_MINUTES / 60)];
    if (active) times.scrollTop = active.offsetTop - times.clientHeight / 2 + active.offsetHeight / 2;
  }

  // Раскрытие от кнопки: вниз, а если места нет — вверх; по краям окна прижимаем.
  const rect = anchor.getBoundingClientRect();
  const width = panel.offsetWidth;
  const height = panel.offsetHeight;
  const left = Math.max(8, Math.min(rect.left, window.innerWidth - width - 8));
  let top = rect.bottom + 4;
  if (top + height > window.innerHeight - 8) top = Math.max(8, rect.top - height - 4);
  panel.style.left = `${left}px`;
  panel.style.top = `${top}px`;

  backdrop.addEventListener('mousedown', (e) => { if (e.target === backdrop) close(); });
  document.addEventListener('keydown', onKey, true);
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
    this.updateRowToolbar();
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
    // «Изменить»/«Удалить» работают по выделению — их доступность меняется вместе с ним.
    this.updateRowToolbar();
  }

  /// Результат запроса выделяется строками целиком: правка и удаление работают
  /// по записи, и блок из нескольких ячеек показывал бы охват, которого у операции
  /// нет. На странице данных выделение остаётся поячеечным — там по нему работает
  /// inline-редактор, и видно, какую ячейку откроет Enter.
  get rowSelection() {
    return this.mode === 'query';
  }

  isSelected(r, c) {
    if (!this.selAnchor || !this.selFocus) return false;
    const r1 = Math.min(this.selAnchor.r, this.selFocus.r);
    const r2 = Math.max(this.selAnchor.r, this.selFocus.r);
    if (r < r1 || r > r2) return false;
    if (this.rowSelection) return true;
    const c1 = Math.min(this.selAnchor.c, this.selFocus.c);
    const c2 = Math.max(this.selAnchor.c, this.selFocus.c);
    return c >= c1 && c <= c2;
  }

  copySelection() {
    if (!this.selAnchor || !this.selFocus) return;
    const r1 = Math.min(this.selAnchor.r, this.selFocus.r);
    const r2 = Math.max(this.selAnchor.r, this.selFocus.r);
    // Копируется ровно то, что выделено: в режиме строк — все колонки записи.
    const c1 = this.rowSelection ? 0 : Math.min(this.selAnchor.c, this.selFocus.c);
    const c2 = this.rowSelection ? this.columns.length - 1 : Math.max(this.selAnchor.c, this.selFocus.c);
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

  connectStream(executionId, source) {
    this.closeStream();
    this.reset();
    this.setQuerySource(source);
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

  // ---------------- Правка строк результата запроса (режим query) ----------------
  // Пакета изменений здесь нет: в панели результатов нет кнопки «Сохранить», и
  // накопленные правки было бы негде показать. Каждое окно применяется сразу.

  /// Таблица, из которой пришли строки текущего результата (editor.js разобрал SQL).
  /// null — запрос сложнее простого SELECT: правка недоступна.
  setQuerySource(source) {
    this.tableMeta = null;
    const d = this.el.dataset;
    if (source && source.table && source.schema) {
      d.dsId = source.dsId;
      d.schema = source.schema;
      d.table = source.table;
      if (source.db) d.database = source.db; else delete d.database;
      // Диалект нужен форме строки: тип «date» в разных СУБД означает разное.
      if (source.dialect) d.dialect = source.dialect; else delete d.dialect;
      // Метаданные подтягиваются заранее: к моменту клика по кнопке они уже есть.
      this.loadTableMeta().then(() => this.updateRowToolbar());
    } else {
      delete d.schema;
      delete d.table;
    }
    this.updateRowToolbar();
  }

  /// Кнопки правки в полосе вкладок панели результатов (Pages/Editor/Index.cshtml).
  rowActionButtons() {
    const host = this.el.closest('.results-panel');
    return host ? host.querySelectorAll('[data-row-action]') : [];
  }

  canEditRows() {
    return this.mode === 'query' && !!this.tableMeta && !this.tableMeta.readOnly && !this.saving;
  }

  updateRowToolbar() {
    const canEdit = this.canEditRows();
    const hasRow = !!this.selAnchor && !!this.selFocus;
    for (const btn of this.rowActionButtons()) {
      // aria-disabled, а не disabled: кнопка иконочная и обязана оставаться
      // наводимой, иначе её назначение узнать неоткуда (как в тулбаре редактора).
      const needsRow = btn.dataset.rowAction !== 'insert';
      btn.setAttribute('aria-disabled', String(!canEdit || (needsRow && !hasRow)));
    }
  }

  /// Ключевые колонки, которыми можно адресовать строку выборки. Псевдоколонка
  /// (ctid/__ROWID) в результат запроса не попадает, поэтому таблица без первичного
  /// ключа отсюда неправима — молча слать UPDATE/DELETE без WHERE недопустимо.
  queryRowKeys() {
    const keys = this.keyColumnNames();
    if (!keys || !keys.length) return null;
    return keys.every((name) => this.colIndexByName(name) >= 0) ? keys : null;
  }

  selectedRowRange() {
    if (!this.selAnchor || !this.selFocus) return null;
    const r1 = Math.min(this.selAnchor.r, this.selFocus.r);
    const r2 = Math.max(this.selAnchor.r, this.selFocus.r);
    return r2 < this.rows.length ? [r1, r2] : null;
  }

  /// Отправка правок результата запроса и перечитывание выборки.
  async applyRowEdits(edits, okMessage) {
    this.saving = true;
    this.updateRowToolbar();
    try {
      const res = await fetch('/api/data/edit', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          dsId: this.el.dataset.dsId,
          db: this.el.dataset.database || null,
          edits,
        }),
      });
      let d = null;
      try { d = await res.json(); } catch (_) { /* не JSON */ }
      if (!res.ok) {
        toast((d && d.error) || `Ошибка сохранения (HTTP ${res.status}).`);
        return false;
      }
      const failures = (d.results || []).filter((x) => !x.success);
      if (failures.length) {
        for (const f of failures.slice(0, 5)) toast(f.error);
        if (failures.length > 5) toast(`…и ещё ошибок: ${failures.length - 5}.`);
        if (d.rolledBack) toast('Все изменения отменены (откат транзакции).', 'info');
        return false;
      }
      toast(okMessage, 'info');
      if (d.inTransaction && !d.committed)
        toast('Изменения применены в открытой транзакции — не забудьте выполнить COMMIT.', 'info');
      // Перечитывание — повтор запроса вкладки: своего источника страниц у грида
      // результатов нет. Запрос берётся из-под курсора, как по кнопке «Выполнить».
      if (window.WebDbEditor && typeof window.WebDbEditor.runActive === 'function')
        window.WebDbEditor.runActive(false);
      return true;
    } catch (_) {
      toast('Сеть недоступна — изменения не сохранены.');
      return false;
    } finally {
      this.saving = false;
      this.updateRowToolbar();
    }
  }

  /// Кнопка «Удалить строки»: удаление сразу после подтверждения.
  deleteSelectedQueryRows() {
    if (!this.canEditRows()) return;
    const keys = this.queryRowKeys();
    if (!keys) {
      toast('В выборке нет первичного ключа таблицы — адресовать строку нечем.');
      return;
    }
    const range = this.selectedRowRange();
    if (!range) { toast('Сначала выделите строки, которые нужно удалить.', 'info'); return; }
    const [r1, r2] = range;
    const count = r2 - r1 + 1;
    if (!window.confirm(`Удалить строк: ${count}? Данные будут удалены из таблицы «${this.el.dataset.schema}.${this.el.dataset.table}».`))
      return;
    const edits = [];
    for (let r = r1; r <= r2; r++) {
      edits.push({
        schema: this.el.dataset.schema,
        table: this.el.dataset.table,
        kind: 'delete',
        keyValues: this.rowKeyValues(r, keys),
        changedValues: null,
      });
    }
    this.applyRowEdits(edits, `Удалено строк: ${count}.`);
  }

  /// Значение из инлайн-помощника: снимает NULL, если он стоял, и помечает поле
  /// заполненным — иначе при вставке колонка ушла бы со значением по умолчанию.
  setFieldValue(field, value) {
    if (field.nullBox && field.nullBox.checked) {
      field.nullBox.checked = false;
      field.input.disabled = false;
    }
    field.input.value = value;
    field.touched = true;
    field.input.focus();
  }

  /// Кнопки-помощники поля по типу колонки: UUID генерируется, дата и время
  /// выбираются системным календарём (<input type=datetime-local>) — свой
  /// календарь ради этого не нужен.
  fieldHelpers(field) {
    const type = String(field.type || '').toLowerCase();

    if (/^uuid\b/.test(type)) {
      const btn = helperButton(ICONS.generate, 'Сгенерировать UUID');
      btn.addEventListener('click', () => this.setFieldValue(field, newUuid()));
      return [btn];
    }

    if (/timestamp|datetime|^date\b/.test(type)) {
      // В PostgreSQL date хранит только дату, в Oracle DATE — дату со временем.
      const dateOnly = this.el.dataset.dialect === 'postgres' && /^date$/.test(type);
      const tip = dateOnly ? 'Выбрать дату' : 'Выбрать дату и время';
      const btn = helperButton(ICONS.calendar, tip);
      btn.addEventListener('click', () => openDateTimePicker(
        btn, field.input.value, !dateOnly, (value) => this.setFieldValue(field, value)));
      return [btn];
    }

    return [];
  }

  /// Модальное окно строки: kind = 'insert' (пустая форма) | 'update' (выделенная строка).
  openRowForm(kind) {
    if (!this.canEditRows()) return;
    const meta = this.tableMeta;
    const isUpdate = kind === 'update';

    let rowIndex = -1;
    let keys = null;
    if (isUpdate) {
      keys = this.queryRowKeys();
      if (!keys) { toast('В выборке нет первичного ключа таблицы — адресовать строку нечем.'); return; }
      const range = this.selectedRowRange();
      if (!range) { toast('Сначала выделите строку, которую нужно изменить.', 'info'); return; }
      rowIndex = range[0];
    }

    // Правим то, что видно: при UPDATE — колонки выборки, при INSERT — все колонки
    // таблицы (незаполненные получат значение по умолчанию).
    const fields = [];
    if (isUpdate) {
      this.columns.forEach((col, c) => {
        const colMeta = meta.columns.find((x) => x.name === col.name);
        if (!colMeta) return;                                   // выражение (count(*), a+b) — не колонка таблицы
        if (colMeta.isGenerated) return;
        if (meta.rowAddressColumn && col.name === meta.rowAddressColumn) return;
        fields.push({ name: col.name, type: col.dataType || colMeta.dataType, colMeta, value: this.rows[rowIndex][c] });
      });
    } else {
      meta.columns.forEach((colMeta) => {
        if (colMeta.isGenerated) return;
        fields.push({ name: colMeta.name, type: colMeta.dataType, colMeta, value: null });
      });
    }
    if (!fields.length) { toast('В таблице нет колонок, доступных для правки.'); return; }

    const overlay = document.createElement('div');
    overlay.className = 'wdb-popup-overlay';
    const popup = document.createElement('div');
    popup.className = 'wdb-popup';

    const title = document.createElement('div');
    title.className = 'wdb-popup-title';
    title.textContent = (isUpdate ? 'Изменение строки — ' : 'Новая строка — ')
      + `${this.el.dataset.schema}.${this.el.dataset.table}`;

    const form = document.createElement('div');
    form.className = 'wdb-popup-form';
    for (const f of fields) {
      const label = document.createElement('label');
      label.className = 'name';
      label.textContent = f.name;
      if (f.type) {
        const type = document.createElement('span');
        type.className = 'type';
        type.textContent = f.type;
        label.appendChild(type);
      }

      const input = document.createElement('input');
      input.type = 'text';
      input.value = f.value === null || f.value === undefined ? '' : String(f.value);
      label.htmlFor = input.id = `wdb-field-${fields.indexOf(f)}`;

      // Помощник поля (генератор UUID, календарь) — своя колонка, пустая у
      // остальных типов: иначе поля разъезжались бы по ширине.
      const helper = document.createElement('span');
      helper.className = 'helper';

      let nullBox = null;
      let nullCell = document.createElement('span');
      if (!f.colMeta || f.colMeta.isNullable) {
        nullCell = document.createElement('label');
        nullCell.className = 'nullbox';
        nullCell.title = 'Записать NULL вместо значения';
        nullBox = document.createElement('input');
        nullBox.type = 'checkbox';
        nullBox.checked = isUpdate && (f.value === null || f.value === undefined);
        nullCell.append(nullBox, document.createTextNode('NULL'));
        nullBox.addEventListener('change', () => {
          f.touched = true;
          input.disabled = nullBox.checked;
          if (!nullBox.checked) input.focus();
        });
        input.disabled = nullBox.checked;
      }
      form.append(label, input, helper, nullCell);

      input.addEventListener('input', () => { f.touched = true; });
      f.input = input;
      f.nullBox = nullBox;
      for (const node of this.fieldHelpers(f)) helper.appendChild(node);
    }

    const hint = document.createElement('div');
    hint.className = 'wdb-popup-hint';
    hint.textContent = isUpdate
      ? 'Отправляются только изменённые колонки.'
      : 'Незаполненные колонки получат значение по умолчанию.';

    const footer = document.createElement('div');
    footer.className = 'wdb-popup-footer';
    const spacer = document.createElement('span');
    spacer.className = 'spacer';
    // Кнопки прижаты вправо, primary — крайняя справа.
    const cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'btn btn-icon';
    cancelBtn.setAttribute('aria-label', 'Отмена');
    cancelBtn.setAttribute('data-tip', 'Закрыть окно, ничего не меняя');
    cancelBtn.innerHTML = ICONS.close;
    const okBtn = document.createElement('button');
    okBtn.type = 'button';
    okBtn.className = 'btn btn-primary btn-icon';
    okBtn.setAttribute('aria-label', isUpdate ? 'Сохранить изменения' : 'Вставить строку');
    okBtn.setAttribute('data-tip', isUpdate ? 'Записать изменения строки в базу' : 'Вставить строку в таблицу');
    okBtn.innerHTML = isUpdate ? ICONS.save : ICONS.confirm;
    footer.append(spacer, cancelBtn, okBtn);

    popup.append(title, form, hint, footer);
    overlay.appendChild(popup);
    document.body.appendChild(overlay);

    const close = () => { overlay.remove(); this.viewport.focus(); };

    const submit = () => {
      const changed = {};
      for (const f of fields) {
        const isNull = f.nullBox && f.nullBox.checked;
        const next = isNull ? null : f.input.value;
        if (isUpdate) {
          const was = f.value === undefined ? null : f.value;
          const same = next === null ? was === null : (was !== null && String(was) === next);
          if (!same) changed[f.name] = next;
        } else if (f.touched) {
          changed[f.name] = next;
        }
      }
      if (isUpdate && !Object.keys(changed).length) { toast('Строка не изменена.', 'info'); return; }

      const edit = {
        schema: this.el.dataset.schema,
        table: this.el.dataset.table,
        kind: isUpdate ? 'update' : 'insert',
        keyValues: isUpdate ? this.rowKeyValues(rowIndex, keys) : null,
        changedValues: changed,
      };
      okBtn.setAttribute('aria-disabled', 'true');
      this.applyRowEdits([edit], isUpdate ? 'Строка изменена.' : 'Строка добавлена.').then((ok) => {
        if (ok) close(); else okBtn.removeAttribute('aria-disabled');
      });
    };

    okBtn.addEventListener('click', () => {
      if (okBtn.getAttribute('aria-disabled') !== 'true') submit();
    });
    cancelBtn.addEventListener('click', close);
    overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) close(); });
    overlay.addEventListener('keydown', (e) => {
      e.stopPropagation(); // Esc/Enter принадлежат окну, а не гриду под ним
      if (e.key === 'Escape') { close(); e.preventDefault(); }
      else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { submit(); e.preventDefault(); }
    });

    const first = fields.find((f) => !f.input.disabled);
    if (first) { first.input.focus(); first.input.select(); } else cancelBtn.focus();
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
  const { executionId, gridTarget, source } = e.detail || {};
  if (!executionId) return;
  let el = gridTarget ? document.querySelector(gridTarget) : null;
  if (!el) el = document.querySelector('[data-result-grid]:not([data-mode="table"])');
  if (!el) el = document.querySelector('[data-result-grid]');
  if (!el) { toast('На странице нет грида результатов.'); return; }
  initGrid(el).connectStream(executionId, source);
});

// Кнопки правки строк в полосе вкладок панели результатов.
document.addEventListener('click', (e) => {
  const btn = e.target.closest ? e.target.closest('[data-row-action]') : null;
  if (!btn || btn.getAttribute('aria-disabled') === 'true') return;
  const host = btn.closest('.results-panel');
  const el = host && host.querySelector('[data-result-grid]');
  const grid = el && grids.get(el);
  if (!grid) return;
  e.preventDefault();
  if (btn.dataset.rowAction === 'delete') grid.deleteSelectedQueryRows();
  else grid.openRowForm(btn.dataset.rowAction);
});

window.WebDbGrid = { initAll, initGrid, get: (el) => grids.get(el) };
