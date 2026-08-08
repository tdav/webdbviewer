// Общие мелочи UI: индикатор загрузки HTMX, тосты об ошибках (на русском),
// переключатель темы с сохранением в cookie.

const THEME_COOKIE = 'webdb-theme';
const DARK_CLASS = 'theme-dark';

// --- Стили тостов и индикатора загрузки (self-contained) ---
const APP_CSS = `
#wdb-loading{position:fixed;top:0;left:0;height:3px;width:100%;z-index:9999;display:none;overflow:hidden;background:transparent}
#wdb-loading .bar{height:100%;width:30%;background:#4a7dfc;animation:wdb-slide 1s linear infinite}
@keyframes wdb-slide{0%{transform:translateX(-100%)}100%{transform:translateX(400%)}}
#wdb-toasts{position:fixed;right:16px;bottom:16px;z-index:10000;display:flex;flex-direction:column;gap:8px;max-width:380px}
.wdb-toast{padding:10px 14px;border-radius:6px;color:#fff;font:13px/1.4 system-ui,sans-serif;box-shadow:0 4px 12px rgba(0,0,0,.25);cursor:pointer;word-break:break-word}
.wdb-toast.error{background:#c0392b}
.wdb-toast.warning{background:#b9770e}
.wdb-toast.info{background:#2c6e49}
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

document.addEventListener('htmx:beforeRequest', () => {
  pendingRequests++;
  ensureLoader().style.display = 'block';
});

document.addEventListener('htmx:afterRequest', () => {
  pendingRequests = Math.max(0, pendingRequests - 1);
  if (pendingRequests === 0) ensureLoader().style.display = 'none';
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

// --- Тема: cookie + класс body + событие для редактора/грида ---
function readCookie(name) {
  const m = document.cookie.match(new RegExp('(?:^|;\\s*)' + name + '=([^;]*)'));
  return m ? decodeURIComponent(m[1]) : null;
}

function applyTheme(theme) {
  document.body.classList.toggle(DARK_CLASS, theme === 'dark');
  document.dispatchEvent(new CustomEvent('webdb:theme-changed', { detail: { theme } }));
}

function currentTheme() {
  return document.body.classList.contains(DARK_CLASS) ? 'dark' : 'light';
}

function toggleTheme() {
  const next = currentTheme() === 'dark' ? 'light' : 'dark';
  // Сохранение выбора в cookie на год.
  document.cookie = `${THEME_COOKIE}=${next}; path=/; max-age=31536000; samesite=lax`;
  applyTheme(next);
}

function initTheme() {
  const saved = readCookie(THEME_COOKIE);
  if (saved) applyTheme(saved);
}

document.addEventListener('DOMContentLoaded', () => {
  injectCss();
  initTheme();
});

// Делегирование клика по переключателю темы (работает и после HTMX-свопов).
document.addEventListener('click', (e) => {
  const btn = e.target.closest ? e.target.closest('[data-theme-toggle]') : null;
  if (btn) {
    e.preventDefault();
    toggleTheme();
  }
});

// Публичный API.
window.WebDb = { toast, toggleTheme, currentTheme };
