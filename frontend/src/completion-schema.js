// Снапшот схемы для локального автодополнения.
// Загружается один раз на пару (датасорс, схема) и живёт в памяти вкладки:
// фильтрация префикса по нему идёт без сети, поэтому попап открывается мгновенно.
// Серверный движок (editor.js) уточняет результат следом — здесь только быстрый черновик.

const cache = new Map();    // ключ -> { data, etag }
const inflight = new Map(); // ключ -> Promise
const timings = [];         // длительности localCompletions, мс

const KEY = (dsId, db, schema) => (dsId || '') + '/' + (db || '') + '/' + (schema || '');

// --- Загрузка ---

// Возвращает true при успехе (в т.ч. 304 — кэш уже актуален) и false при неудаче
// (204, сетевая ошибка, не-2xx) — вызывающая сторона (editor.js) решает по этому
// флагу, можно ли считать ключ загруженным и не повторять попытку.
export async function load(dsId, db, schema) {
  if (!dsId) return false;
  const key = KEY(dsId, db, schema);
  if (inflight.has(key)) return inflight.get(key);

  const promise = (async () => {
    const url = '/api/completion/schema-map?dsId=' + encodeURIComponent(dsId)
      + (schema ? '&schema=' + encodeURIComponent(schema) : '')
      + (db ? '&db=' + encodeURIComponent(db) : '');
    const headers = {};
    const known = cache.get(key);
    if (known && known.etag) headers['If-None-Match'] = known.etag;
    try {
      const res = await fetch(url, { headers });
      if (res.status === 304) return true;        // снапшот в кэше актуален
      if (!res.ok || res.status === 204) return false; // без локального кэша работаем как раньше
      const data = await res.json();
      cache.set(key, { data, etag: res.headers.get('ETag') });
      return true;
    } catch (e) {
      return false; // Сеть недоступна — подсказки просто пойдут только с сервера.
    } finally {
      inflight.delete(key);
    }
  })();

  inflight.set(key, promise);
  return promise;
}

export function reset(dsId, db, schema) {
  cache.delete(KEY(dsId, db, schema));
}

export function stats() {
  if (!timings.length) return { count: 0, p50: 0, p95: 0 };
  const sorted = [...timings].sort((a, b) => a - b);
  const at = (q) => sorted[Math.min(sorted.length - 1, Math.floor(q * sorted.length))];
  return { count: sorted.length, p50: at(0.5), p95: at(0.95) };
}

// --- Правила, общие с сервером ---

// Порт SqlIdentifierQuoting.Quote: кавычки только если имя без них сменит смысл.
const PG_PLAIN = /^[a-z_][a-z0-9_$]*$/;
const ORACLE_PLAIN = /^[A-Z][A-Z0-9_$#]*$/;

function isOracle(dialect) {
  return (dialect || '').toLowerCase().includes('oracle');
}

function quote(identifier, dialect) {
  if (!identifier) return identifier;
  const plain = isOracle(dialect) ? ORACLE_PLAIN.test(identifier) : PG_PLAIN.test(identifier);
  return plain ? identifier : '"' + identifier.replace(/"/g, '""') + '"';
}

// Приоритеты повторяют SemanticCompleter.Priority; boost в CodeMirror — «чем больше, тем выше»,
// поэтому знак инвертируется. Локальный вариант на единицу ниже серверного аналога:
// при совпадении сервер должен побеждать.
const PRIORITY = { scopeColumn: 5, contextTable: 8, otherColumn: 10, routine: 12 };
const boostOf = (priority) => -(priority + 1);

// --- Разбор контекста ---

const ALIAS_STOP = new Set([
  'where', 'on', 'join', 'inner', 'left', 'right', 'full', 'cross', 'natural', 'lateral',
  'group', 'order', 'having', 'limit', 'offset', 'fetch', 'set', 'values', 'using',
  'union', 'except', 'intersect', 'returning', 'window', 'for',
]);

/// Текст statement'а, внутри которого стоит каретка, и позиция каретки в нём.
function currentStatement(text, pos) {
  const before = text.slice(0, pos);
  const start = before.lastIndexOf(';') + 1;
  return { text: text.slice(start), caret: pos - start };
}

/// Таблицы и их алиасы из FROM/JOIN/UPDATE/INTO текущего statement.
function tableRefs(statement) {
  const re = /\b(?:from|join|update|into)\s+((?:[\w$#]+|"[^"]+")(?:\s*\.\s*(?:[\w$#]+|"[^"]+"))?)(?:\s+(?:as\s+)?([a-zA-Z][\w$#]*))?/gi;
  const refs = [];
  let m;
  while ((m = re.exec(statement)) !== null) {
    const parts = m[1].split('.').map((p) => p.trim().replace(/^"|"$/g, ''));
    const name = parts[parts.length - 1];
    const alias = m[2] && !ALIAS_STOP.has(m[2].toLowerCase()) ? m[2] : null;
    refs.push({ name, alias });
  }
  return refs;
}

const TABLE_CONTEXT = new Set(['from', 'join', 'into', 'update', 'table', 'lateral']);

// --- Построение вариантов ---

export function localCompletions({ text, pos, dsId, db, schema, dialect }) {
  const entry = cache.get(KEY(dsId, db, schema));
  if (!entry) return null;

  const started = performance.now();
  const snapshot = entry.data;
  const stmt = currentStatement(text, pos);
  const before = stmt.text.slice(0, stmt.caret);

  // Кавычка входит в границу слова — то же правило, что и у серверного пути в editor.js
  // (context.matchBefore(/[\w"$#]*/)): иначе вставка квотированного идентификатора
  // задваивает открывающую кавычку, когда вариант выбран до ответа сервера.
  const word = /[\w"$#]*$/.exec(before)[0];
  const from = pos - word.length;
  const qualifier = /([\w$#]+|"[^"]+")\s*\.\s*[\w$#]*$/.exec(before);
  const prevWord = (/([a-zA-Z]+)\s+[\w$#]*$/.exec(before) || [])[1];

  const refs = tableRefs(stmt.text);
  const options = [];

  const tableByName = new Map();
  for (const t of snapshot.tables) tableByName.set(t.n.toLowerCase(), t);

  if (qualifier) {
    // «alias.» или «table.» — колонки соответствующей таблицы и ничего больше.
    const q = qualifier[1].replace(/^"|"$/g, '').toLowerCase();
    const ref = refs.find((r) => (r.alias || '').toLowerCase() === q)
      || refs.find((r) => r.name.toLowerCase() === q);
    const table = tableByName.get((ref ? ref.name : q).toLowerCase());
    if (table) pushColumns(options, table, PRIORITY.scopeColumn, dialect);
  } else if (prevWord && TABLE_CONTEXT.has(prevWord.toLowerCase())) {
    // Позиция имени таблицы: только таблицы и вью.
    // ponytail: автоалиас (SemanticCompleter.MakeAlias) здесь раньше добавлялся
    // безусловно, а сервер — только при включённом CompletionOptions.AutoAliasTables
    // (по умолчанию false, /api/completion его не включает). Расхождение — прямой
    // баг: один и тот же вариант вставлял разный текст до/после ответа сервера.
    // Пока сервер не включит опцию, клиент алиас не добавляет.
    pushTables(options, snapshot.tables, dialect);
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
  return options.length ? { from, options } : null;
}

function pushColumns(options, table, priority, dialect) {
  for (const c of table.c || []) {
    options.push({
      label: c.n,
      type: 'property',
      apply: quote(c.n, dialect),
      detail: columnDetail(c),
      info: c.cm || undefined,
      boost: boostOf(priority),
    });
  }
}

function columnDetail(c) {
  const parts = [c.d];
  if (c.pk) parts.push('PK');
  if (!c.nl) parts.push('NOT NULL');
  return parts.join(' · ');
}

function pushTables(options, tables, dialect) {
  for (const t of tables) {
    options.push({
      label: t.n,
      type: 'class',
      apply: quote(t.n, dialect),
      detail: t.t === 'table' ? undefined : t.t,
      info: t.cm || undefined,
      boost: boostOf(PRIORITY.contextTable),
    });
  }
}

function pushRoutines(options, routines, dialect) {
  for (const r of routines || []) {
    options.push({
      label: r.n,
      type: 'function',
      apply: quote(r.n, dialect) + '(',
      detail: r.s || undefined,
      info: r.cm || undefined,
      boost: boostOf(PRIORITY.routine),
    });
  }
}

// Публичный API для editor.js и обработчика кнопки «Обновить метаданные».
window.WebDbCompletion = { load, reset, localCompletions, stats };
