// Общие мелочи UI: индикатор загрузки HTMX, тосты об ошибках (на русском),
// переключатель темы с сохранением в cookie.

// Ключ должен совпадать с антимерцающим скриптом в _Layout.cshtml, который
// выставляет тему до первой отрисовки.
const THEME_KEY = 'wdv-theme';

// --- Стили тостов и индикатора загрузки ---
// Цвета берутся из токенов app.css. Тост и полоса загрузки — overlay,
// поэтому тень здесь разрешена системой; визуальный язык тот же, что у .alert:
// тонированный фон, полная граница семантического цвета, текст того же цвета.
const APP_CSS = `
#wdb-loading{position:fixed;top:0;left:0;height:2px;width:100%;z-index:var(--z-tooltip,70);display:none;overflow:hidden;background:transparent}
#wdb-loading .bar{height:100%;width:30%;background:var(--accent,#ff6b2c);animation:wdb-slide 1s linear infinite}
@keyframes wdb-slide{0%{transform:translateX(-100%)}100%{transform:translateX(400%)}}
/* Блокировщик ввода: перехватывает клики и затеняет страницу, пока идёт загрузка.
   Появляется не сразу (см. BLOCK_DELAY_MS) — на быстром ответе мигание затемнения
   раздражает сильнее, чем случайный клик по ещё не обновившейся разметке. */
#wdb-blocker{
  position:fixed;inset:0;z-index:var(--z-blocker,55);display:none;
  align-items:center;justify-content:center;
  background:var(--overlay-scrim,rgba(10,10,14,.58));cursor:progress
}
#wdb-blocker[data-visible]{display:flex}
#wdb-blocker .ring{
  width:34px;height:34px;border:3px solid var(--accent,#ff6b2c);border-top-color:transparent;
  border-radius:50%;animation:wdb-spin .7s linear infinite
}
@keyframes wdb-spin{to{transform:rotate(360deg)}}
#wdb-toasts{position:fixed;right:16px;bottom:16px;z-index:var(--z-toast,60);display:flex;flex-direction:column;gap:8px;max-width:380px}
.wdb-toast{
  padding:10px 14px;border-radius:var(--radius-md,6px);
  font:13px/1.4 var(--font-ui,system-ui,sans-serif);
  background:var(--bg-panel,#fff);color:var(--text,#1a1a2e);
  border:1px solid var(--border-strong,rgba(26,26,46,.22));
  box-shadow:var(--shadow-overlay-sm,0 4px 12px rgba(0,0,0,.25));
  cursor:pointer;word-break:break-word
}
.wdb-toast.error{background:var(--danger-bg);border-color:var(--danger);color:var(--danger)}
.wdb-toast.warning{background:var(--warning-bg);border-color:var(--warning);color:var(--warning)}
.wdb-toast.info{background:var(--info-bg);border-color:var(--info);color:var(--info)}
@media (prefers-reduced-motion: reduce){
  #wdb-loading .bar{animation:none;width:100%;opacity:.6}
  /* Остановленное кольцо — разорванная окружность: гасим не движение, а вид. */
  #wdb-blocker .ring{animation:none;border-top-color:var(--accent,#ff6b2c);opacity:.5}
}
`;

function injectCss() {
  if (document.getElementById('wdb-app-css')) return;
  const style = document.createElement('style');
  style.id = 'wdb-app-css';
  style.textContent = APP_CSS;
  document.head.appendChild(style);
}

// --- Тосты ---
function toast(message, type) {
  injectCss();
  let container = document.getElementById('wdb-toasts');
  if (!container) {
    container = document.createElement('div');
    container.id = 'wdb-toasts';
    document.body.appendChild(container);
  }
  const el = document.createElement('div');
  el.className = 'wdb-toast ' + (type || 'info');
  el.textContent = message;
  el.addEventListener('click', () => el.remove());
  container.appendChild(el);
  setTimeout(() => el.remove(), 6000);
}

// --- Индикатор загрузки HTMX (учитывает параллельные запросы) ---
let pendingRequests = 0;

function ensureLoader() {
  injectCss();
  let loader = document.getElementById('wdb-loading');
  if (!loader) {
    loader = document.createElement('div');
    loader.id = 'wdb-loading';
    loader.innerHTML = '<div class="bar"></div>';
    document.body.appendChild(loader);
  }
  return loader;
}

// --- Блокировщик ввода на время загрузки ---
// Пока идёт запрос, страница показывает старое содержимое: клик по кнопке или
// ссылке в этот момент уходит в разметку, которая вот-вот будет заменена.
// Оверлей перехватывает такие клики.
//
// Появляется с задержкой: подавляющее большинство htmx-запросов здесь короче
// 200 мс, и мгновенное затемнение на каждый из них читалось бы как моргание.
const BLOCK_DELAY_MS = 200;
// И держится минимум столько же — блокировщик, мелькнувший на один кадр, хуже,
// чем не показанный вовсе.
const BLOCK_MIN_VISIBLE_MS = 250;

let blockTimer = null;
let blockShownAt = 0;
let blockHideTimer = null;

function ensureBlocker() {
  injectCss();
  let blocker = document.getElementById('wdb-blocker');
  if (!blocker) {
    blocker = document.createElement('div');
    blocker.id = 'wdb-blocker';
    // Оверлей — сугубо визуальная преграда: содержание ожидания объявляет
    // aria-busy на body, дублировать его отдельным live-region незачем.
    blocker.setAttribute('aria-hidden', 'true');
    blocker.innerHTML = '<div class="ring"></div>';
    // Клики и колесо гасим здесь же: на оверлее курсор «занято», а не «нажми».
    for (const type of ['click', 'mousedown', 'wheel', 'touchstart']) {
      blocker.addEventListener(type, (e) => { e.preventDefault(); e.stopPropagation(); });
    }
    document.body.appendChild(blocker);
  }
  return blocker;
}

function showBlocker() {
  clearTimeout(blockHideTimer);
  blockHideTimer = null;
  if (blockTimer !== null || ensureBlocker().hasAttribute('data-visible')) return;
  blockTimer = setTimeout(() => {
    blockTimer = null;
    if (pendingRequests === 0) return; // запрос успел завершиться
    ensureBlocker().setAttribute('data-visible', '');
    document.body.setAttribute('aria-busy', 'true');
    blockShownAt = Date.now();
  }, BLOCK_DELAY_MS);
}

function hideBlocker() {
  if (blockTimer !== null) {
    // Запрос уложился в задержку — блокировщик так и не появился.
    clearTimeout(blockTimer);
    blockTimer = null;
    return;
  }
  const blocker = ensureBlocker();
  if (!blocker.hasAttribute('data-visible')) return;
  const shownFor = Date.now() - blockShownAt;
  const remaining = Math.max(0, BLOCK_MIN_VISIBLE_MS - shownFor);
  clearTimeout(blockHideTimer);
  blockHideTimer = setTimeout(() => {
    blockHideTimer = null;
    blocker.removeAttribute('data-visible');
    document.body.removeAttribute('aria-busy');
  }, remaining);
}

document.addEventListener('htmx:beforeRequest', () => {
  pendingRequests++;
  ensureLoader().style.display = 'block';
  showBlocker();
});

document.addEventListener('htmx:afterRequest', () => {
  pendingRequests = Math.max(0, pendingRequests - 1);
  if (pendingRequests === 0) {
    hideBlocker();
    ensureLoader().style.display = 'none';
  }
});

// --- Обработчики ошибок HTMX (тосты на русском) ---
document.addEventListener('htmx:responseError', (e) => {
  const status = e.detail && e.detail.xhr ? e.detail.xhr.status : '';
  toast(`Ошибка сервера${status ? ' (' + status + ')' : ''}. Попробуйте ещё раз.`, 'error');
});

document.addEventListener('htmx:sendError', () => {
  toast('Сеть недоступна: не удалось выполнить запрос.', 'error');
});

document.addEventListener('htmx:timeout', () => {
  toast('Превышено время ожидания ответа сервера.', 'warning');
});

// --- Тема ---
// Единственный владелец темы в приложении: атрибут <html data-theme> плюс
// localStorage. Раньше здесь жила вторая реализация (cookie + body.theme-dark),
// параллельная скрипту в _Layout, и компоненты, смотревшие на body, никогда не
// узнавали о переключении.
function applyTheme(theme) {
  document.documentElement.setAttribute('data-theme', theme);
  document.dispatchEvent(new CustomEvent('webdb:theme-changed', { detail: { theme } }));
}

function currentTheme() {
  return document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
}

function toggleTheme() {
  const next = currentTheme() === 'dark' ? 'light' : 'dark';
  try { localStorage.setItem(THEME_KEY, next); } catch { /* приватный режим — выбор не переживёт перезагрузку */ }
  applyTheme(next);
}

document.addEventListener('DOMContentLoaded', injectCss);

// Делегирование клика по переключателю темы (работает и после HTMX-свопов).
document.addEventListener('click', (e) => {
  const btn = e.target.closest ? e.target.closest('[data-theme-toggle]') : null;
  if (btn) {
    e.preventDefault();
    toggleTheme();
  }
});

// --- Подсказки для иконочных кнопок ---
// Кнопки в интерфейсе подписаны иконкой, поэтому подсказка — не украшение, а
// единственный видимый способ узнать, что делает кнопка. Нативный title для
// этого не подходит: задержка около секунды, ничего не показывает при переходе
// фокуса с клавиатуры и не поддаётся стилизации.
// Доступное имя кнопки живёт в aria-label; сама подсказка помечена aria-hidden,
// иначе скринридер прочитал бы одно и то же дважды.
const TIP_DELAY_MS = 350;

let tipEl = null;
let tipTarget = null;
let tipTimer = 0;

// Подсказка объявлена popover, чтобы попадать в top layer. Обычный элемент с
// любым z-index уходит под нативный <dialog>: модалка открывается через
// showModal() и вместе с backdrop поднимается в слой, недостижимый для z-index.
const supportsPopover = typeof HTMLElement !== 'undefined'
  && typeof HTMLElement.prototype.showPopover === 'function';

function ensureTip() {
  if (tipEl) return tipEl;
  tipEl = document.createElement('div');
  tipEl.id = 'wdv-tooltip';
  tipEl.setAttribute('aria-hidden', 'true');
  if (supportsPopover) tipEl.setAttribute('popover', 'manual');
  document.body.appendChild(tipEl);
  return tipEl;
}

/** Поднимает подсказку в top layer; без поддержки popover остаётся обычным слоем. */
function raiseTip(el) {
  if (!supportsPopover) return;
  // Повторный showPopover на уже открытом элементе бросает InvalidStateError.
  try { if (!el.matches(':popover-open')) el.showPopover(); } catch { /* уже открыт */ }
}

function lowerTip(el) {
  if (!supportsPopover) return;
  try { if (el.matches(':popover-open')) el.hidePopover(); } catch { /* уже скрыт */ }
}

/** Ставит подсказку под элементом, а если снизу не помещается — над ним. */
function placeTip(target) {
  const el = ensureTip();
  const r = target.getBoundingClientRect();
  const t = el.getBoundingClientRect();
  const gap = 6;
  let top = r.bottom + gap;
  if (top + t.height > window.innerHeight - 4) top = r.top - t.height - gap;
  // По горизонтали центрируем, но не даём выйти за края окна.
  let left = r.left + r.width / 2 - t.width / 2;
  left = Math.max(4, Math.min(left, window.innerWidth - t.width - 4));
  el.style.top = `${Math.round(top)}px`;
  el.style.left = `${Math.round(left)}px`;
}

function showTip(target, immediate) {
  const text = target.getAttribute('data-tip');
  if (!text) return;
  tipTarget = target;
  const render = () => {
    if (tipTarget !== target || !target.isConnected) return;
    const el = ensureTip();
    el.textContent = text;
    const keys = target.getAttribute('data-tip-keys');
    if (keys) {
      el.appendChild(document.createTextNode(' '));
      const kbd = document.createElement('kbd');
      kbd.textContent = keys;
      el.appendChild(kbd);
    }
    // Показываем после измерения: иначе первый кадр окажется в углу экрана.
    el.style.visibility = 'hidden';
    raiseTip(el);
    el.setAttribute('data-visible', '');
    placeTip(target);
    el.style.visibility = '';
  };
  clearTimeout(tipTimer);
  // При переходе фокуса подсказка нужна сразу: пользователь уже выбрал кнопку.
  if (immediate) render();
  else tipTimer = setTimeout(render, TIP_DELAY_MS);
}

function hideTip() {
  clearTimeout(tipTimer);
  tipTarget = null;
  if (!tipEl) return;
  tipEl.removeAttribute('data-visible');
  lowerTip(tipEl);
}

function tipSource(node) {
  return node && node.closest ? node.closest('[data-tip]') : null;
}

document.addEventListener('mouseover', (e) => {
  const target = tipSource(e.target);
  if (target && target !== tipTarget) showTip(target, false);
});
document.addEventListener('mouseout', (e) => {
  if (tipSource(e.target) === tipTarget) hideTip();
});
document.addEventListener('focusin', (e) => {
  const target = tipSource(e.target);
  if (target) showTip(target, true);
});
document.addEventListener('focusout', hideTip);
// Клик уже сообщил результат действия — подсказка становится помехой.
document.addEventListener('click', hideTip);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideTip(); });
// Скролл и ресайз сдвигают кнопку, а fixed-подсказка осталась бы на месте.
window.addEventListener('scroll', hideTip, true);
window.addEventListener('resize', hideTip);

// --- Раскрытие узлов навигатора ---
// Делегирование, потому что htmx подгружает поддеревья после первого раскрытия.
function toggleTreeNode(label) {
  const li = label.parentElement;
  if (!li) return;
  const ul = li.querySelector(':scope > ul');
  // Списка ещё нет — значит htmx только что запросил детей, и узел раскрывается.
  const expanded = ul ? (ul.hidden = !ul.hidden, !ul.hidden) : true;
  label.setAttribute('aria-expanded', String(expanded));
  li.classList.toggle('expanded', expanded);
}

document.addEventListener('click', (e) => {
  if (!e.target.closest) return;
  const label = e.target.closest('.tree-label[aria-expanded]');
  // Ссылка внутри строки («открыть данные», имя таблицы) ведёт на свою страницу
  // и не должна попутно сворачивать узел.
  if (!label || e.target.closest('a')) return;
  toggleTreeNode(label);
});

document.addEventListener('keydown', (e) => {
  if (e.key !== 'Enter' && e.key !== ' ') return;
  if (!e.target.closest) return;
  const label = e.target.closest('.tree-label[aria-expanded]');
  if (!label || label !== document.activeElement) return;
  e.preventDefault(); // пробел иначе прокручивает навигатор
  toggleTreeNode(label);
});

// Публичный API.
// blockScreen/unblockScreen — для загрузок мимо htmx (fetch в гриде и редакторе).
// Счётчик общий с htmx-запросами, поэтому вложенные ожидания не гасят друг друга.
function blockScreen() {
  pendingRequests++;
  showBlocker();
}

function unblockScreen() {
  pendingRequests = Math.max(0, pendingRequests - 1);
  if (pendingRequests === 0) hideBlocker();
}

window.WebDb = { toast, toggleTheme, currentTheme, blockScreen, unblockScreen };

// Обновление метаданных схемы: сбрасываем серверный кэш и клиентский снапшот,
// затем грузим его заново. Делегирование на document — кнопка живёт в HTMX-фрагменте
// тулбара и пересоздаётся при каждой смене датасорса или базы.
document.addEventListener('click', async (e) => {
  const btn = e.target.closest ? e.target.closest('[data-action="refresh-metadata"]') : null;
  // aria-disabled не блокирует клик сам по себе — повторный клик во время
  // запроса отсекаем здесь же (см. такой же приём в editor.js).
  if (!btn || btn.getAttribute('aria-disabled') === 'true') return;
  e.preventDefault();

  const ds = document.querySelector('[data-role="datasource-select"]');
  const schemaSelect = document.querySelector('[data-role="schema-select"]');
  const dbSelect = document.querySelector('[data-role="database-select"]');
  const dsId = ds && ds.value;
  const schema = schemaSelect && schemaSelect.value ? schemaSelect.value : null;
  const db = dbSelect && dbSelect.value ? dbSelect.value : null;
  if (!dsId) return;

  btn.setAttribute('aria-disabled', 'true');
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
    btn.removeAttribute('aria-disabled');
  }
});
