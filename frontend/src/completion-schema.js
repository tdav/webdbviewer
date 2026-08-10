// Снапшот схемы для локального автодополнения.
// Загружается один раз на пару (датасорс, схема) и живёт в памяти вкладки:
// фильтрация префикса по нему идёт без сети, поэтому попап открывается мгновенно.
// Серверный движок (editor.js) уточняет результат следом — здесь только быстрый черновик.

const cache = new Map();    // ключ -> { data, etag }
const inflight = new Map(); // ключ -> Promise
const timings = [];         // длительности localCompletions, мс

const KEY = (dsId, schema) => (dsId || '') + '/' + (schema || '');

// --- Загрузка ---

export async function load(dsId, schema) {
  if (!dsId) return;
  const key = KEY(dsId, schema);
  if (inflight.has(key)) return inflight.get(key);

  const promise = (async () => {
    const url = '/api/completion/schema-map?dsId=' + encodeURIComponent(dsId)
      + (schema ? '&schema=' + encodeURIComponent(schema) : '');
    const headers = {};
    const known = cache.get(key);
    if (known && known.etag) headers['If-None-Match'] = known.etag;
    try {
      const res = await fetch(url, { headers });
      if (res.status === 304) return;          // снапшот в кэше актуален
      if (!res.ok || res.status === 204) return; // без локального кэша работаем как раньше
      const data = await res.json();
      cache.set(key, { data, etag: res.headers.get('ETag') });
    } catch (e) {
      // Сеть недоступна — подсказки просто пойдут только с сервера.
    } finally {
      inflight.delete(key);
    }
  })();

  inflight.set(key, promise);
  return promise;
}

export function reset(dsId, schema) {
  cache.delete(KEY(dsId, schema));
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

// Порт SemanticCompleter.MakeAlias: первые буквы snake/camel-сегментов, при коллизии — цифра.
// Расхождение с серверным правилом — дефект: пользователь получил бы разный текст
// в зависимости от того, успел ли прийти ответ сервера.
export function makeAlias(table, taken) {
  let initials = '';
  let newSegment = true;
  for (const ch of table) {
    // \p{Nd} — та же Unicode-категория (Decimal Digit Number), что распознаёт
    // char.IsDigit в C#: не только ASCII 0-9, но и, например, арабо-индийские цифры.
    if (ch === '_' || ch === '$' || ch === '#' || /\p{Nd}/u.test(ch)) {
      newSegment = true;
      continue;
    }
    const isLetter = /\p{L}/u.test(ch);
    if (newSegment && isLetter) {
      initials += ch.toLowerCase();
      newSegment = false;
    } else if (isLetter && ch === ch.toUpperCase() && ch !== ch.toLowerCase()) {
      initials += ch.toLowerCase();
    }
  }
  const base = initials.length ? initials : 't';
  let alias = base;
  let n = 2;
  while (taken.has(alias)) alias = base + n++;
  taken.add(alias);
  return alias;
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

export function localCompletions({ text, pos, dsId, schema, dialect }) {
  const entry = cache.get(KEY(dsId, schema));
  if (!entry) return null;

  const started = performance.now();
  const snapshot = entry.data;
  const stmt = currentStatement(text, pos);
  const before = stmt.text.slice(0, stmt.caret);

  const word = /[\w$#]*$/.exec(before)[0];
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
    // Позиция имени таблицы: только таблицы и вью, с автоалиасом.
    const taken = new Set(refs.map((r) => r.alias).filter(Boolean).map((a) => a.toLowerCase()));
    const autoAlias = prevWord.toLowerCase() !== 'into' && prevWord.toLowerCase() !== 'update'
      && prevWord.toLowerCase() !== 'table';
    pushTables(options, snapshot.tables, dialect, autoAlias ? taken : null);
  } else {
    // Общий случай: колонки таблиц текущего statement, затем таблицы и функции.
    for (const ref of refs) {
      const table = tableByName.get(ref.name.toLowerCase());
      if (table) pushColumns(options, table, PRIORITY.scopeColumn, dialect);
    }
    pushTables(options, snapshot.tables, dialect, null);
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

function pushTables(options, tables, dialect, takenAliases) {
  for (const t of tables) {
    let apply = quote(t.n, dialect);
    if (takenAliases) apply = apply + ' ' + makeAlias(t.n, takenAliases);
    options.push({
      label: t.n,
      type: 'class',
      apply,
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
window.WebDbCompletion = { load, reset, localCompletions, stats, makeAlias };
