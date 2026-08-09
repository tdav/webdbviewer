# Доработка Code Completion в SQL-редакторе

Дата: 2026-08-10. Ветка: `feature/code-completion`.

## Задача

Довести автодополнение SQL до рабочего состояния для Oracle и PostgreSQL: вызов по Ctrl+Space,
кэширование команд диалекта наравне с таблицами и колонками, устранение пробелов, из-за которых
подсказки не появляются вовсе.

## Исходное состояние

Работает и переписыванию не подлежит:

- `WebDbViewer.Completion.CompletionEngine` — antlr4-c3 поверх грамматик PostgreSQL и PL/SQL,
  семантика v1 (`SemanticCompleter`, `ScopeAnalyzer`: FROM/JOIN, CTE, подзапросы, LATERAL,
  FK-сниппеты для `ON`, автоалиасы, раскрытие `t.*`, humps-матчинг), откат на v0-эвристику.
- `WebDbViewer.Metadata.MetadataCache` — снапшоты схем per-datasource, single-flight через
  `Lazy(ExecutionAndPublication)`, TTL, stale-while-revalidate, persistent-снапшоты в метабазе,
  поисковый индекс (trie + humps).
- `frontend/src/editor.js` — CodeMirror 6, `autocompletion({ override })`, debounce 250 мс,
  отмена устаревших запросов через `AbortController`.

## Дефекты, которые закрываем

1. **Ctrl+Space проходит через debounce.** `completionKeymap` из `basicSetup` привязывает
   Ctrl+Space к `startCompletion`, но источник подсказок ставит `setTimeout` на 250 мс независимо
   от `context.explicit`. Дополнительно: `clearTimeout` предыдущего таймера оставляет прошлый
   `Promise` неразрешённым.
2. **Команды диалекта не кэшируются.** `GrammarAnalyzer.Analyze` на каждое нажатие строит лексер,
   парсер и `CodeCompletionCore.CollectCandidates` по всему тексту до каретки. На длинном скрипте
   парсится весь предыдущий текст. Кэша результата нет; при исключении внутри anlr4-c3 список
   ключевых слов оказывается пустым и подсказок нет вовсе.
3. **Кэш метаданных не прогревается.** `IMetadataCache.WarmupAsync` реализован, но не вызывается
   ни из одного места Web-слоя. Первое обращение к автодополнению ждёт полную интроспекцию схемы.
4. **Oracle без явной схемы остаётся без метаданных.** `CompletionEngine.TryGetSchemaSnapshotAsync`
   подставляет `public` только для PostgreSQL; для Oracle при `DefaultSchema == null` возвращает
   `null`, и подсказки объектов не формируются.
5. **Функции и процедуры не предлагаются.** `SchemaSnapshot.Routines` загружаются обоими
   провайдерами (с `ArgumentsSignature`, `ReturnType`, комментарием) и индексируются для поиска,
   но ни `CompletionEngine`, ни `SemanticCompleter` их не читают.

## Решение

### 1. Ctrl+Space — `frontend/src/editor.js`

Источник подсказок разделяет два пути:

- `context.explicit === true` — HTTP-запрос немедленно, без `setTimeout`;
- набор текста — прежний debounce 250 мс.

Промис, чей таймер сброшен более новым вызовом, резолвится в `null` вместо того, чтобы остаться
неразрешённым.

Клавиша не переопределяется: `completionKeymap` из `basicSetup` уже даёт Ctrl+Space, и
`Prec.highest`-биндинги редактора (Ctrl+Enter, Alt+X) с ней не конфликтуют.

### 2. Кэш команд диалекта — `WebDbViewer.Completion/GrammarAnalyzer.cs`

- **Обрезка префикса до текущего statement.** Перед разбором префикс режется существующим
  `StatementSplitter`: анализируется только statement, внутри которого стоит каретка. Снимает
  зависимость стоимости от длины скрипта и убирает влияние синтаксиса предыдущих запросов на
  восстановление после ошибок.
- **LRU-кэш `GrammarCandidates`** на 128 записей, ключ — (диалект, текст префикса statement'а).
  Результат зависит только от ключа, поэтому кэш статический и общий для процесса.
- **Статический словарь ключевых слов диалекта.** Полный набор из `parser.Vocabulary`, строится
  один раз лениво отдельно для PostgreSQL и PL/SQL. Используется как источник подсказок, когда
  antlr4-c3 бросил исключение или вернул пустой набор токенов.

### 3. Прогрев кэша метаданных

`POST /api/completion/warmup { dsId, schema }` вызывает `IMetadataCache.WarmupAsync` и сразу
возвращает `202` — прогрев идёт в фоне и его сбой не влияет на ответ. Фронт дёргает endpoint при
инициализации редактора и при смене датасорса или схемы в тулбаре.

### 4. Схема по умолчанию для Oracle

`CompletionEndpoints` уже располагает `DataSourceConfig`. Схема вычисляется там:
`request.DefaultSchema ?? (Oracle ? config.Username.ToUpperInvariant() : "public")`. Для Oracle
имя пользователя подключения и есть его схема; верхний регистр соответствует тому, как Oracle
хранит имена в `ALL_*`.

### 5. Функции и процедуры в подсказках

`RoutineInfo` из снапшотов попадают в кандидаты:

- `SemanticCompleter` — в колоночных контекстах (`SELECT`, `WHERE`, `HAVING`, `ON`, `ORDER BY`,
  `GROUP BY`) и при квалификаторе `schema.`;
- `CompletionEngine` — в v0-эвристике, там же где колонки.

Формат элемента: `Kind = "function"`, `InsertText = <квотированное имя>(`, `Detail` — сигнатура
аргументов, `Documentation` — комментарий и тип возврата. Приоритет — между колонками и таблицами.

Фронт уже отображает `function` (в `KIND_MAP` есть отображение на тип CodeMirror `function`).

## Тесты

`tests/WebDbViewer.Tests.Unit/Completion/` поверх существующего `FakeMetadataCache`:

- функции и процедуры попадают в подсказки в SELECT-контексте, `InsertText` заканчивается на `(`;
- повторный вызов `GrammarAnalyzer` с тем же префиксом даёт тот же результат (кэш не искажает);
- словарь ключевых слов непуст для обоих диалектов;
- разбор второго statement не зависит от синтаксиса первого;
- элементы с `Kind = "function"` не появляются в позиции имени таблицы после `FROM`.

Существующие тесты правятся только если ломаются изменениями.

## Границы

Не входит в этот заход: встроенные функции диалекта (`NVL`, `DECODE`, `COALESCE`, `string_agg`),
типы данных для `CAST`/`::`, Oracle-специфика (`dual`, `ROWNUM`, `seq.NEXTVAL`, пакеты `DBMS_*`),
шаблонные сниппеты запросов, колонки в `VALUES (…)`, ранжирование по истории использования.
`ScopeAnalyzer`, семантика v1 и `MetadataCache` не переписываются.

## Проверка

Юнит-тесты покрывают движок и грамматический анализ. Связка «Ctrl+Space → HTTP → провайдер»
требует живого подключения к Oracle и PostgreSQL и проверяется вручную.
