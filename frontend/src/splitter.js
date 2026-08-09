// Разделители рабочей области (как в DBeaver): перетаскивание мышью/тачем,
// сохранение размеров между сессиями, управление с клавиатуры.
//
// Разметка (Pages/Editor/Index.cshtml):
//   .workbench            → grid: [навигатор] [.splitter-v] [.editor-and-results]
//   .editor-and-results   → grid: [редактор] [.splitter-h] [панель результатов]
//
// Меняем grid-template-columns/rows контейнера — сами панели трогать не нужно.

const STORAGE_KEY = 'wdv-layout';

// Минимальные размеры панелей, px. Ниже — панель становится нечитаемой.
const MIN_NAV_WIDTH = 180;
const MIN_WORKAREA_WIDTH = 320;
const MIN_EDITOR_HEIGHT = 120;
const MIN_RESULTS_HEIGHT = 100;

// Значения по умолчанию совпадают с CSS (.workbench / .editor-and-results в app.css).
const DEFAULT_NAV_WIDTH = 280;
const DEFAULT_RESULTS_FRACTION = 0.4;

// Шаг изменения размера стрелками клавиатуры.
const KEYBOARD_STEP = 16;

function readLayout() {
  try {
    return JSON.parse(localStorage.getItem(STORAGE_KEY)) || {};
  } catch {
    return {};
  }
}

function writeLayout(patch) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify({ ...readLayout(), ...patch }));
  } catch {
    /* localStorage недоступен (приватный режим) — размеры просто не переживут перезагрузку */
  }
}

function clamp(value, min, max) {
  // При очень узком окне max может оказаться меньше min — тогда отдаём min.
  return max < min ? min : Math.min(Math.max(value, min), max);
}

function splitterSize(el) {
  const size = el.classList.contains('splitter-v') ? el.offsetWidth : el.offsetHeight;
  return size || 6;
}

// На узких экранах app.css складывает рабочую область по вертикали: навигатор
// становится верхней полосой, а вертикальный разделитель — горизонтальным.
// Ширина колонок там не имеет смысла, и inline-стиль отсюда её ломал: навигатор
// растягивался на всю ширину, и в трек разделителя записывалось значение в
// сотни пикселей. Ниже брейкпоинта раскладкой владеет только CSS.
// Значение обязано совпадать с медиазапросом в app.css.
const STACKED_LAYOUT = window.matchMedia('(max-width: 860px)');

function isStacked() {
  return STACKED_LAYOUT.matches;
}

/** Возвращает раскладку под управление CSS: снимает inline-переопределение. */
function releaseTracks(el, property) {
  el.style.removeProperty(property);
}

/** Уведомляет редактор и грид, что доступная площадь изменилась. */
function notifyResize() {
  document.dispatchEvent(new CustomEvent('wdv:layout-resized'));
  window.dispatchEvent(new Event('resize'));
}

// --- Ширина навигатора (вертикальный разделитель) ---

function applyNavWidth(workbench, splitter, width) {
  if (isStacked()) {
    releaseTracks(workbench, 'grid-template-columns');
    // В стопке высота навигатора задана в CSS и не регулируется: разделитель
    // остаётся видимой границей, но перестаёт быть управляющим элементом.
    splitter.removeAttribute('aria-valuenow');
    splitter.setAttribute('aria-disabled', 'true');
    return width;
  }
  splitter.removeAttribute('aria-disabled');
  const gap = splitterSize(splitter);
  const max = workbench.clientWidth - gap - MIN_WORKAREA_WIDTH;
  const value = Math.round(clamp(width, MIN_NAV_WIDTH, max));
  workbench.style.gridTemplateColumns = `${value}px ${gap}px 1fr`;
  splitter.setAttribute('aria-valuenow', String(value));
  return value;
}

function currentNavWidth(workbench) {
  const navigator = workbench.firstElementChild;
  return navigator ? navigator.getBoundingClientRect().width : DEFAULT_NAV_WIDTH;
}

// --- Высота панели результатов (горизонтальный разделитель) ---

function applyResultsHeight(container, splitter, height) {
  const gap = splitterSize(splitter);
  const max = container.clientHeight - gap - MIN_EDITOR_HEIGHT;
  const value = Math.round(clamp(height, MIN_RESULTS_HEIGHT, max));
  container.style.gridTemplateRows = `minmax(${MIN_EDITOR_HEIGHT}px, 1fr) ${gap}px ${value}px`;
  splitter.setAttribute('aria-valuenow', String(value));
  return value;
}

function currentResultsHeight(container) {
  const results = container.lastElementChild;
  return results
    ? results.getBoundingClientRect().height
    : container.clientHeight * DEFAULT_RESULTS_FRACTION;
}

/**
 * Навешивает перетаскивание на разделитель.
 * @param {HTMLElement} splitter элемент-разделитель
 * @param {'vertical'|'horizontal'} orientation ориентация разделителя
 * @param {(clientX:number, clientY:number)=>number} sizeFromPointer размер панели по позиции курсора
 * @param {(size:number)=>number} apply применяет размер, возвращает применённое значение
 * @param {(size:number)=>void} persist сохраняет размер
 * @param {()=>number} readCurrent текущий размер (для клавиатуры)
 * @param {()=>void} reset сброс к значению по умолчанию
 */
function makeDraggable(splitter, orientation, sizeFromPointer, apply, persist, readCurrent, reset) {
  splitter.setAttribute('role', 'separator');
  splitter.setAttribute('aria-orientation', orientation);
  if (!splitter.hasAttribute('tabindex')) splitter.setAttribute('tabindex', '0');

  const bodyCursorClass = orientation === 'vertical' ? 'wdv-resizing-col' : 'wdv-resizing-row';
  let dragging = false;
  let pendingPointer = null;
  let frame = 0;
  let lastSize = null;

  // Применение размера привязано к кадру: за одно перетаскивание браузер шлёт сотни
  // pointermove, а каждый notifyResize заставляет CodeMirror и грид пересчитать раскладку.
  function flush() {
    frame = 0;
    if (!pendingPointer) return;
    lastSize = apply(sizeFromPointer(pendingPointer.x, pendingPointer.y));
    pendingPointer = null;
    notifyResize();
  }

  function onPointerMove(e) {
    if (!dragging) return;
    // Предотвращает выделение текста и прокрутку страницы на тач-устройствах.
    e.preventDefault();
    pendingPointer = { x: e.clientX, y: e.clientY };
    if (!frame) frame = requestAnimationFrame(flush);
  }

  function stopDragging(e) {
    if (!dragging) return;
    dragging = false;

    // Досчитываем последнюю позицию указателя, чтобы не потерять хвост движения.
    if (frame) {
      cancelAnimationFrame(frame);
      frame = 0;
    }
    if (pendingPointer) {
      lastSize = apply(sizeFromPointer(pendingPointer.x, pendingPointer.y));
      pendingPointer = null;
    }
    // В localStorage пишем один раз по окончании перетаскивания, а не на каждый кадр.
    if (lastSize !== null) {
      persist(lastSize);
      lastSize = null;
    }

    splitter.classList.remove('dragging');
    document.body.classList.remove('wdv-resizing', bodyCursorClass);

    if (e && e.pointerId !== undefined) {
      try {
        if (splitter.hasPointerCapture(e.pointerId)) splitter.releasePointerCapture(e.pointerId);
      } catch {
        /* захват уже снят браузером */
      }
    }
    document.removeEventListener('pointermove', onPointerMove);
    document.removeEventListener('pointerup', stopDragging);
    notifyResize();
  }

  splitter.addEventListener('pointerdown', (e) => {
    // Только основная кнопка мыши; на тач/пере button тоже 0.
    if (e.button !== 0) return;
    dragging = true;
    splitter.classList.add('dragging');
    document.body.classList.add('wdv-resizing', bodyCursorClass);

    // Захват указателя: перетаскивание не теряется, когда курсор уходит за пределы разделителя.
    let captured = false;
    try {
      splitter.setPointerCapture(e.pointerId);
      captured = true;
    } catch {
      captured = false;
    }
    if (!captured) {
      // Запасной вариант, если захват недоступен — слушаем указатель на документе.
      document.addEventListener('pointermove', onPointerMove);
      document.addEventListener('pointerup', stopDragging);
    }
    e.preventDefault();
  });

  splitter.addEventListener('pointermove', onPointerMove);
  splitter.addEventListener('pointerup', stopDragging);
  splitter.addEventListener('pointercancel', stopDragging);
  splitter.addEventListener('lostpointercapture', stopDragging);

  // Двойной клик — вернуть размер по умолчанию.
  splitter.addEventListener('dblclick', () => {
    reset();
    notifyResize();
  });

  // Клавиатура: стрелки двигают разделитель, Home/End — к минимуму/максимуму.
  splitter.addEventListener('keydown', (e) => {
    const decrease = orientation === 'vertical' ? 'ArrowLeft' : 'ArrowUp';
    const increase = orientation === 'vertical' ? 'ArrowRight' : 'ArrowDown';

    let target = null;
    if (e.key === decrease) target = readCurrent() - KEYBOARD_STEP;
    else if (e.key === increase) target = readCurrent() + KEYBOARD_STEP;
    else if (e.key === 'Home') target = 0;
    else if (e.key === 'End') target = Number.MAX_SAFE_INTEGER;
    if (target === null) return;

    e.preventDefault();
    persist(apply(target));
    notifyResize();
  });
}

function initVerticalSplitter() {
  const splitter = document.getElementById('splitter-v');
  const workbench = splitter && splitter.closest('.workbench');
  if (!workbench) return;

  // Последняя ширина в развёрнутой раскладке. В стопке навигатор занимает всю
  // ширину окна, поэтому читать её из DOM нельзя — при возврате к широкому
  // экрану панель раздулась бы на весь экран.
  let wideWidth = readLayout().navWidth;
  if (typeof wideWidth !== 'number') wideWidth = currentNavWidth(workbench);

  const apply = (width) => {
    const applied = applyNavWidth(workbench, splitter, width);
    if (!isStacked()) wideWidth = applied;
    return applied;
  };
  apply(wideWidth);

  makeDraggable(
    splitter,
    'vertical',
    // Ширина навигатора = расстояние от левого края рабочей области до курсора.
    (clientX) => clientX - workbench.getBoundingClientRect().left,
    apply,
    (width) => writeLayout({ navWidth: width }),
    () => currentNavWidth(workbench),
    () => writeLayout({ navWidth: apply(DEFAULT_NAV_WIDTH) })
  );

  // При изменении размера окна пересчитываем ограничения (панель могла стать шире окна).
  window.addEventListener('resize', () => apply(isStacked() ? wideWidth : currentNavWidth(workbench)));
  // Пересечение брейкпоинта: либо отдать раскладку CSS, либо вернуть сохранённую ширину.
  STACKED_LAYOUT.addEventListener('change', () => apply(wideWidth));
}

function initHorizontalSplitter() {
  const splitter = document.getElementById('splitter-h');
  const container = splitter && splitter.closest('.editor-and-results');
  if (!container) return;

  const apply = (height) => applyResultsHeight(container, splitter, height);
  const saved = readLayout().resultsHeight;
  apply(typeof saved === 'number' ? saved : currentResultsHeight(container));

  makeDraggable(
    splitter,
    'horizontal',
    // Высота результатов = расстояние от курсора до нижнего края области.
    (_clientX, clientY) => container.getBoundingClientRect().bottom - clientY,
    apply,
    (height) => writeLayout({ resultsHeight: height }),
    () => currentResultsHeight(container),
    () => writeLayout({
      resultsHeight: apply(container.clientHeight * DEFAULT_RESULTS_FRACTION)
    })
  );

  window.addEventListener('resize', () => apply(currentResultsHeight(container)));
}

function init() {
  initVerticalSplitter();
  initHorizontalSplitter();
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init);
} else {
  init();
}
