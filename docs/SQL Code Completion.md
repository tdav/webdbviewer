# План: удобный, отзывчивый и эффективный SQL Code Completion для PostgreSQL и Oracle в webdbviewer

## TL;DR
- **Рекомендуется гибридная архитектура (Вариант C):** список таблиц/вью грузится один раз при открытии редактора в серверный `HybridCache` и отдаётся на клиент как JSON schema-map для Monaco/CodeMirror provider (фильтрация префикса на клиенте, p95 < 50 мс); колонки конкретной таблицы догружаются **lazy** по `alias.`/`FROM` через отдельный JSON endpoint. Это ровно тот путь, который прошли pgAdmin 4 (уход на клиентский CodeMirror-autocomplete) и DataGrip (introspection levels + on-demand догрузка деталей).
- **Лечение «перегруза» Oracle:** отказ от загрузки всех колонок всех схем сразу; переход на `USER_*`/фильтр по `OWNER` (не `DBA_*`), хинт `RESULT_CACHE`, серверный кэш и lazy-колонки. Причина медлительности подтверждена JetBrains: «Oracle catalogs are generally pretty slow» и сложная проверка прав на каждый объект в `ALL_*`.
- **Ключевые числа:** debounce до попапа 150–250 мс, client interaction delay ~75 мс, max рендер 50 предложений, TTL кэша таблиц 10–30 мин / колонок 30–60 мин + ручной Refresh; цель p95 lazy-запроса < 500 мс (тёплый кэш), время до первого попапа < 1 с.

---

## Диагностика текущего состояния

> Диагностика ниже написана до чтения кода. Фактическое состояние — в разделе «Что реализовано» в конце файла.

> **Важное ограничение прозрачности:** прямой доступ к `https://github.com/tdav/webdbviewer` из среды исследования оказался **невозможен** — GitHub, `raw.githubusercontent.com` и `api.github.com` блокируют автоматический доступ, а репозиторий не проиндексирован ни поисковиками, ни LLM-зеркалами (uithub/gitingest/deepwiki). Поэтому диагностика ниже построена на (а) заявленных пользователем фактах и (б) типовых антипаттернах SQL-автодополнения. **Фаза 0 плана — самостоятельно подтвердить эти пункты по коду** (репозиторий у вас есть локально; в air-gapped сети это делается напрямую).

Заявлено пользователем: стек **ASP.NET Core (.NET 10) + HTMX**; гибрид **EF Core + Dapper**; текущая реализация Oracle «сильно перегружена»; автодополнение нужно в первую очередь для `SELECT` и прочих SQL-команд.

Типовые причины «перегруза» Oracle, которые нужно проверить в коде (и которые почти наверняка присутствуют, судя по формулировке проблемы):
- Загрузка **всех** таблиц и колонок **всех** схем сразу через `ALL_TAB_COLUMNS`/`ALL_OBJECTS`/`DBA_*` при открытии редактора.
- Отсутствие серверного кэша метаданных → повтор тяжёлых словарных запросов на каждый вызов.
- **N+1**: отдельный запрос колонок на каждую таблицу.
- Отсутствие debounce и отмены устаревших запросов на клиенте.
- Запрос на сервер на каждое нажатие клавиши (антипаттерн старого pgAdmin — см. ниже).

**Чек-лист подтверждения (Фаза 0):**
1. `.csproj` → `TargetFramework`; пакеты `Oracle.ManagedDataAccess.Core`, `Npgsql`, `Dapper`, наличие `Microsoft.Extensions.Caching.Hybrid`.
2. Фронтенд-редактор: Monaco / CodeMirror / Ace; где регистрируется completion provider и как он ходит на сервер.
3. Найти весь SQL к словарям: `ALL_`, `DBA_`, `USER_`, `information_schema`, `pg_catalog`.
4. Есть ли `IMemoryCache`/`HybridCache`; есть ли lazy-загрузка колонок; есть ли debounce/`AbortController`/`CancellationToken`.

---

## Key Findings — как это сделано у других инструментов

**pgAdmin 4 (прямой прецедент вашей задачи).** Разработчик Aditya Toshniwal (EnterpriseDB) в рассылке pgadmin-hackers 02.01.2024 описал переход со старой серверной схемы на клиентскую: *«Right now, we send the query to the backend and the backend sends the suggestions... This can be very slow for remote PostgreSQL servers as it queries everytime to the server for data and requires an active connection. Also, not a good user experience... The data from the backend is loaded once the query tool opens.»* Akshay Joshi (04.01.2024): *«I think we should use the new approach.»* Вывод: индустрия признала «сервер на каждое нажатие» плохим паттерном и ушла к загрузке схемы один раз + клиентский CodeMirror 6 autocomplete.

**JetBrains DataGrip / IntelliJ — introspection levels + lazy detail.** Метаданные грузятся уровнями: Level 1 — только имена объектов; детали (колонки, исходники) догружаются **on demand** при открытии объекта, автоматически, без действий пользователя. Пороги auto-select (по документации DataGrip): для **текущей** схемы — Level 1 при числе объектов > 3 000, Level 2 при числе объектов с исходниками > 500, Level 3 в остальных случаях; для **прочих** схем — > 300 и > 50 соответственно. Default = «Auto select» начиная с версии 2023.3. Причина именно такого дизайна прямо озвучена JetBrains («What's New in DataGrip 2023.3»): *«DataGrip takes a long time to introspect schemas in Oracle because Oracle catalogs are generally pretty slow.»* Автор Oracle-интроспектора (блог 06.12.2023) уточняет: *«The ALL catalog lists only those objects to which the current user has access... the permission system in Oracle is very complex, and it takes a long time to verify a user's access for each object.»*

**DBeaver.** Три completion-движка: **Semantic** (рекомендуемый; анализ структуры SQL и лексических scope), **Legacy** (по позиции в запросе), **Combined**; плюс **Hippie** (по словам текущего файла). Настройки в *Preferences → SQL Editor → Code Completion*: «Enable auto activation», «Auto activation delay», выбор движка. Пользователи в конфигурациях наблюдают auto-activation delay ~500 мс, а Semantic-движок дополнительно добавляет задержку планирования семантического анализа (наблюдения >500 мс в discussion #38932).

**SSMS / Azure Data Studio.** IntelliSense при подключении запускает фоновый поток, который запрашивает системные каталоги (`sys.objects`, `sys.columns`, `sys.schemas`) и строит **in-memory словарь** всех видимых объектов. Ручной сброс — `Ctrl+Shift+R` (Edit → IntelliSense → Refresh Local Cache); в ADS refresh автоматический/через Command Palette. Требует прав `VIEW DEFINITION`. Вывод: кэш метаданных + явная кнопка refresh — отраслевой стандарт.

**CodeMirror 6 `@codemirror/lang-sql`.** `schemaCompletionSource` принимает готовую schema-map (schema→table→columns), поддерживает `defaultSchema`, `defaultTable`, `upperCaseKeywords`. Параметры автодополнения: `activateOnTyping`, `closeOnBlur`, `interactionDelay` (по умолчанию **75 мс** — «commands relating to an open completion only take effect 75 milliseconds after the completion opened, so that key presses made before the user is aware of the tooltip don't go to the tooltip»), `maxRenderedOptions` (по умолчанию **50** — «The maximum number of options to render to the DOM»).

**Monaco Editor.** `registerCompletionItemProvider` с `triggerCharacters` (точка и т.д.); фильтрация префикса встроена и идёт на клиенте; ранжирование через `sortText`/`filterText`/`preselect`. Есть известные race-condition баги при быстром вводе (issues #2437, #2787) — требуется отмена запросов. `editor.suggest.maxVisibleSuggestions` был удалён в 0.23.0 (виджет виртуализируется).

**SQL LSP-серверы (sqls, postgres-lsp, langium-sql).** AST-based анализ контекста курсора; схема грузится из словаря БД или из файлов. postgres-lsp построен на tree-sitter-postgres. Это ориентир для контекстно-зависимых предложений, если позже захотите вынести логику в LSP.

---

## Details

### Таблица рекомендуемых числовых параметров

| Параметр | Рекоменд. значение | Ориентир у других инструментов |
|---|---|---|
| Auto-activation delay (debounce до попапа) | **150–250 мс** | VS Code/Monaco `quickSuggestionsDelay` = 10 мс (default); NetBeans `completion-auto-popup-delay` = 250 мс; DBeaver ~500 мс в конфиге |
| Client interaction delay (защита от «проскока» клавиш) | **75 мс** | CodeMirror `interactionDelay` = 75 мс (default) |
| Мин. длина префикса для имён | **1–2 символа**; после `.` и после ключевого слова — **0** | Общая практика |
| Trigger characters | **`.`**, пробел после `FROM`/`JOIN`/`,`, `(` | Monaco `triggerCharacters` |
| Max предложений (рендер в DOM) | **50** (фильтр по префиксу до этого) | CodeMirror `maxRenderedOptions` = 50 |
| Латентность фильтрации на клиенте | **p95 < 50 мс** | Nielsen: 0.1 с = «reacting instantaneously» |
| Латентность серверного lazy-запроса колонок | **p50 < 200 мс, p95 < 500 мс** (тёплый кэш); cold < 1 с | Nielsen: 1 с = «flow of thought stays uninterrupted» |
| TTL кэша метаданных | таблицы **10–30 мин**; колонки **30–60 мин**; + ручной Refresh | SSMS/ADS ручной refresh; DataGrip smart refresh |
| Батч lazy-колонок | по одной таблице/алиасу при обращении | DataGrip on-demand |
| Порог «грузить всю схему на клиент» | **≤ ~2 000–3 000 таблиц**; выше — hybrid/lazy | DataGrip Level 1 при > 3 000 объектов (текущая схема), > 300 (прочие) |

> Обоснование latency-бюджетов — три порога Джейкоба Нильсена (NN/g «Response Time Limits»): 0.1 с — «system is reacting instantaneously»; 1.0 с — «user's flow of thought to stay uninterrupted»; 10 с — предел удержания внимания. Отсюда: клиентская фильтрация должна укладываться в 0.1 с, а любой серверный round-trip — уверенно в 1 с.

### Оптимизированные запросы к словарям (готовы к использованию)

**Oracle — список таблиц/вью текущей схемы (warm-up при коннекте):**
```sql
SELECT /*+ RESULT_CACHE */ table_name AS name, 'TABLE' AS obj_type FROM user_tables
UNION ALL
SELECT /*+ RESULT_CACHE */ view_name  AS name, 'VIEW'  AS obj_type FROM user_views;
```
Доступ к чужим схемам — только `ALL_*` строго с фильтром по `OWNER` (никаких `DBA_*`):
```sql
SELECT /*+ RESULT_CACHE */ owner, table_name
FROM   all_tables
WHERE  owner = :owner;
```

**Oracle — колонки ОДНОЙ таблицы (lazy, по обращению к алиасу):**
```sql
SELECT column_name, data_type, nullable, data_length, data_precision, column_id
FROM   user_tab_columns          -- или all_tab_columns WHERE owner = :owner
WHERE  table_name = :table_name
ORDER  BY column_id;
```
Комментарии для tooltip (отдельно/lazy): `user_col_comments`, `user_tab_comments` (для многоязычных — ваша таблица переводов по `table_name`+`column_name`).

**PostgreSQL — таблицы/вью (pg_catalog, warm-up):**
```sql
SELECT n.nspname AS schema_name, c.relname AS name, c.relkind
FROM   pg_class c
JOIN   pg_namespace n ON n.oid = c.relnamespace
WHERE  c.relkind IN ('r','v','m','p')          -- table, view, matview, partitioned
AND    n.nspname NOT IN ('pg_catalog','information_schema')
ORDER  BY 1, 2;
```

**PostgreSQL — колонки одной таблицы (lazy, быстро через OID):**
```sql
SELECT a.attname AS column_name,
       format_type(a.atttypid, a.atttypmod) AS data_type,
       NOT a.attnotnull AS is_nullable
FROM   pg_attribute a
WHERE  a.attrelid = to_regclass(:qualified_name)::oid
AND    a.attnum > 0 AND NOT a.attisdropped
ORDER  BY a.attnum;
```
`information_schema` — только если нужна портируемость; учтите, что оно фильтрует по правам и не показывает materialized views.

### Целевая архитектура (Вариант C — гибрид, рекомендован)

- **Вариант A (всё на клиенте):** прост, нулевая сетевая задержка при фильтрации; ломается на Oracle-масштабе (EBS — >20k таблиц): большой payload и память браузера. Годен только для малых/средних схем.
- **Вариант B (сервер на каждый запрос):** сетевая задержка на каждое нажатие, race conditions, нагрузка на словарь; **отвергнут** — это ровно тот путь, от которого ушёл pgAdmin.
- **Вариант C (гибрид) — выбор:**
  1. **Warm-up при открытии редактора:** одним запросом грузим таблицы/вью текущей схемы + ключевые слова + функции → `HybridCache` (сервер) → JSON schema-map на клиент.
  2. **Колонки — lazy:** при вводе `alias.` или при разборе `FROM`/`JOIN` клиент дергает `GET /api/completion/columns?conn=..&schema=..&table=..`; ответ кэшируется и на сервере, и в JS-Map.
  3. **Фильтрация префикса и ранжирование — на клиенте** (провайдер Monaco/CodeMirror), без сети → p95 < 50 мс.
- **HTMX для автодополнения в редакторе не подходит** (HTMX = swap HTML-фрагментов; здесь нужны структурированные данные и клиентская фильтрация). Нужен **прямой JSON endpoint + completion provider редактора**. HTMX оставить для остального UI приложения.
- **Отмена устаревших запросов:** `AbortController` на клиенте + `CancellationToken` на сервере; ответы, пришедшие после нового ввода, игнорировать (закрывает Monaco race-condition баги).
- **Серверный кэш — `HybridCache`** (GA в .NET 9, доступен в .NET 10). По Microsoft Learn: *«even without an IDistributedCache implementation, the HybridCache service still provides in-process caching and stampede protection»*; stampede protection = «for a given key, only one concurrent caller runs the factory; everyone else waits for that single result» — устраняет thundering herd при одновременных промахах. Tag-инвалидация — логическая (помечает записи как stale); учтите, что в .NET 10 tag-API помечены experimental (`#pragma warning disable EXTEXP0018`). Ключи: `{connectionId}:{schema}:tables` и `{connectionId}:{schema}:{table}:columns`. Под ваши соглашения — sealed-класс `SchemaMetadataCache` с primary constructor, Result pattern, Dapper для словарных запросов.

### UX и качество предложений

- **Ранжирование (`sortText`):** колонки таблиц из текущего `FROM`/`JOIN` → алиасы → таблицы default-схемы → таблицы прочих схем → ключевые слова/функции.
- **Контекст курсора:** после `SELECT` — колонки; после `FROM`/`JOIN` — таблицы; после `alias.` — колонки этой таблицы; после `WHERE` — колонки + функции; внутри CTE/подзапросов — соответствующий scope. Для этого — error-tolerant/partial-парсинг (ANTLR грамматики `PlSqlParser`/`PostgreSQLParser` из antlr/grammars-v4, либо sql-parser-cst). На старте достаточно лёгкого regex/токенайзера контекста, AST — в Фазе 3.
- **Регистр идентификаторов:** Oracle по умолчанию UPPER, PostgreSQL lower/snake_case; квотирование `"..."` только когда идентификатор не совпадает с нормализованным регистром. Fuzzy/subsequence matching + поддержка snake_case и CamelCase.
- **Автоматизация:** авто-alias при выборе таблицы, генерация `JOIN ... ON` по внешним ключам, разворот `SELECT *` в список колонок, сниппеты.
- **Tooltip:** тип колонки, nullable, PK/FK, комментарии (в т.ч. многоязычные).
- **Frecency:** ранжирование по частоте/недавности использования объектов конкретным пользователем.

---

## Recommendations — поэтапный план внедрения

**Фаза 0 — Подтверждение (0.5–1 день).** Пройти чек-лист по коду; замерить текущие latency и размер payload; включить логирование словарных запросов Oracle (сколько их на сессию редактора). **Порог перехода к Фазе 1:** подтверждено, что колонки грузятся не-lazy и/или нет кэша.

**Фаза 1 — Quick wins (максимальный эффект / минимум затрат):**
1. Убрать загрузку всех колонок всех схем; при коннекте грузить только список таблиц/вью текущей схемы.
2. Ввести `HybridCache` для таблиц и колонок (ключи по connection+schema+table, stampede protection «из коробки»).
3. Oracle: перейти на `USER_*`/фильтр `OWNER` + `RESULT_CACHE`, полностью убрать `DBA_*`.
4. Клиент: debounce 150–250 мс + `AbortController` + `maxRenderedOptions` 50.

**Фаза 2 — Среднесрочное:**
5. Lazy-загрузка колонок по `alias.`/`FROM` через JSON endpoint (устраняет N+1).
6. Контекстно-зависимые предложения (разбор позиции курсора) + ранжирование `sortText`.
7. Кнопка «Refresh metadata» + TTL-инвалидация (таблицы 10–30 мин, колонки 30–60 мин).

**Фаза 3 — Долгосрочное:**
8. FK-aware `JOIN ... ON`, разворот `SELECT *`, авто-alias, сниппеты.
9. Tooltip с типами/комментариями (включая многоязычные); frecency-ранжирование.
10. Error-tolerant AST-парсер (ANTLR/sql-parser-cst) для корректных scope CTE/подзапросов; опц. вынос в LSP.

**Что даёт наибольший ROI:** пункты 1–4 (Фаза 1) закрывают заявленный «перегруз» Oracle практически полностью при минимальных изменениях.

---

## Метрики успеха и способ измерения
- **p95 фильтрации на клиенте < 50 мс** — замер `performance.now()` вокруг provider.
- **p95 lazy-запроса колонок < 500 мс** (тёплый кэш) — серверные метрики endpoint (напр. через `Activity`/логи).
- **Число словарных запросов Oracle на сессию редактора — снижение на порядок** (устранение N+1 и повторов) — сравнить логи до/после.
- **Payload первичной schema-map < 1 МБ (gzip)** для типовой схемы — размер ответа warm-up.
- **Cache hit rate метаданных > 90%** — счётчики `HybridCache`.
- **Время до первого попапа после коннекта < 1 с** — end-to-end замер.

---

## Caveats и подводные камни
- **Диагностика репозитория не верифицирована** — код `tdav/webdbviewer` не был доступен из среды исследования; Фаза 0 обязательна для подтверждения стека и точных мест «перегруза».
- **Права доступа:** `ALL_*` (Oracle) и `VIEW DEFINITION` (SQL Server-подобные) могут не отдавать часть объектов — корректно обрабатывать пустые результаты, не падать.
- **Синонимы и большое число объектов** в Oracle замедляют даже отфильтрованные запросы; `RESULT_CACHE` помогает только при повторных одинаковых запросах.
- **Инвалидация при DDL:** без явного Refresh кэш устаревает (как в SSMS) — обязательна кнопка и разумный TTL.
- **Race conditions попапа** при быстром вводе (известные баги Monaco #2437/#2787) — обязательна отмена запросов (`AbortController`/`CancellationToken`).
- **HybridCache tag-инвалидация в .NET 10 — experimental** (требует подавления `EXTEXP0018`); для базовой инвалидации по TTL и по ключу это не нужно.
- **Air-gapped среда (Узбекистан, госсеть):** Monaco/CodeMirror и все npm-зависимости, шрифты, воркеры — только self-hosted, без внешних CDN. Проверить, что редактор и его web worker (Monaco) отдаются с вашего сервера.

---

## Что реализовано (2026-08-10)

Этот раздел описывает фактическое состояние кода в ветке `feature/completion-client-cache` на момент написания. Все утверждения проверены по коду, а не по плану или спеке — ссылки на конкретные файлы приведены при каждом пункте.

### Было закрыто до этой итерации

К моменту начала работы над клиентским кэшем (база `95acc7e`) в кодовой базе уже был реализован полноценный серверный движок автодополнения — большая часть рекомендаций исследования выше на самом деле уже выполнена:

- **Серверный ANTLR-движок** — `src/WebDbViewer.Completion/`: `GrammarAnalyzer.cs` (грамматика + antlr4-c3 `CodeCompletionCore`), `SemanticCompleter.cs` (семантика алиасов, CTE, подзапросов), `CompletionEngine.cs`.
- **Кэш метаданных с single-flight и TTL** — `src/WebDbViewer.Metadata/MetadataCache.cs`: `Lazy` + CAS против одновременных промахов, stale-while-refresh, персистентный снапшот, поиск по trie.
- **Oracle без `DBA_*`** — `src/WebDbViewer.Providers.Oracle/OracleProvider.cs` не содержит ни одного обращения к `DBA_*`-представлениям (проверено grep'ом по файлу); используются только `ALL_*` с фильтром по `:owner`.
- **Отсутствие N+1 по колонкам** — интроспекция схемы идёт batch-запросами (объекты, комментарии таблиц, комментарии колонок, ограничения, колонки), а не по одному запросу на таблицу.
- **Debounce и отмена устаревших запросов** — `frontend/src/editor.js:19`: `const COMPLETION_DEBOUNCE_MS = 250;`, плюс `AbortController` в `fetchCompletions` и немедленный путь для Ctrl+Space.
- **Авто-alias при выборе таблицы** — `SemanticCompleter` (серверная часть) и, с этой итерации, его JS-порт `makeAlias` в `frontend/src/completion-schema.js`.
- **FK-сниппеты** (`JOIN … ON` по внешним ключам, раскрытие `t.*`) — часть `SemanticCompleter`.
- **Signature help** — `src/WebDbViewer.Completion/SignatureHelp.cs` + `POST /api/completion/signature` (`CompletionEndpoints.cs`).
- **Frecency** — `src/WebDbViewer.Completion/RecentObjects.cs`, LRU на 20 таблиц на датасорс.

Подробное сопоставление пунктов исследования с кодом — таблица в разделе 1 `docs/superpowers/specs/2026-08-10-sql-code-completion-design.md`.

### Добавлено в этой итерации

Предметом этой итерации был перенос фильтрации префикса на клиент (устранение round-trip'а на каждое нажатие клавиши — архитектура «Вариант B» из диагностики выше, которая всё ещё была в силе на момент начала работы). Коммиты: `95acc7e..bd3cc46` (ветка `feature/completion-client-cache`).

- **Endpoint снапшота схемы** — `GET /api/completion/schema-map` в `src/WebDbViewer.Web/Api/CompletionEndpoints.cs` (`SchemaMapAsync`), заменил `POST /api/completion/warmup` (сам HTTP-endpoint удалён; метод `IMetadataCache.WarmupAsync` остался — используется в тестах и в `refresh`). DTO — `src/WebDbViewer.Web/Api/SchemaMapDto.cs`: короткие ключи JSON (`n`, `t`, `c`, `d`, `pk`, `nl`, `cm`), пропуск пустых комментариев, порог `partial` при `MaxTables = 2000` таблиц или `MaxColumns = 50000` колонок (при превышении колонки в ответе не отдаются). Кэширование через `ETag`/`If-None-Match`: `SchemaMapDto.ETagFor` строит ETag из `SchemaSnapshot.VersionHash`, при совпадении сервер отвечает `304` без тела.
- **Клиентский модуль снапшота** — `frontend/src/completion-schema.js`, экспортируется как `window.WebDbCompletion` (`load`, `reset`, `localCompletions`, `stats`, `makeAlias`). Хранит снапшот в `Map` по ключу `dsId/schema`, дедуплицирует параллельные загрузки, копирует серверные правила квотирования идентификаторов (`quote`) и построения алиасов (`makeAlias` — порт `SemanticCompleter.MakeAlias`).
- **Двухфазный источник автодополнения** — `frontend/src/editor.js`, `makeCompletionSource`: фаза 1 отдаёт локальные варианты из снапшота мгновенно (без сети), параллельно с текущим debounce 250 мс уходит запрос на сервер; фаза 2 — когда серверный ответ приходит (сверка по ключу «позиция + текст»), вызывается `startCompletion(view)` и на повторном проходе источник возвращает объединённый список (серверные варианты первыми, локальные добавляются только те, чьих `label` там нет). Прежний вызов прогрева заменён на `loadSchemaMap` (три точки вызова: инициализация редактора, смена схемы, смена датасорса).
- **Кнопка «Обновить метаданные» и endpoint инвалидации** — `POST /api/metadata/refresh` в новом `src/WebDbViewer.Web/Api/MetadataRefreshEndpoints.cs`: `IMetadataCache.InvalidateAsync` синхронно, затем фоновый `WarmupAsync` (ответ `202` сразу, не дожидаясь прогрева). Кнопка в тулбаре редактора (`data-action="refresh-metadata"`) сбрасывает и клиентский снапшот через `window.WebDbCompletion.reset`, и серверный кэш.
- **`RESULT_CACHE` в словарных запросах Oracle — опробован и отклонён по итогам замера.** Хинт `/*+ RESULT_CACHE */` добавлялся в 5 запросов интроспекции схемы (`src/WebDbViewer.Providers.Oracle/OracleProvider.cs`: объекты, комментарии таблиц, комментарии колонок, ограничения, колонки) как эксперимент с заранее оговорённым критерием — оставить хинты, если медиана интроспекции улучшится минимум на 10%. Замер на живом Oracle (`docs/superpowers/reports/2026-08-10-oracle-introspection-timing.md`) показал медиану 401 мс «после» против 387 мс «до» — улучшения нет, критерий не выполнен, поэтому хинты убраны, а файл возвращён к виду коммита `7188027`.
- **Замеры латентности** — сервер: `src/WebDbViewer.Metadata/MetadataCache.cs`, лог уровня `Debug` в `LoadCoreAsync` — `"Интроспекция схемы {Schema} датасорса {DataSourceId}: {ElapsedMs} мс, таблиц {Tables}"`. Клиент: `performance.now()` вокруг `localCompletions`, накопление в `window.WebDbCompletion.stats()` (`{count, p50, p95}`).

### Что сознательно не делалось и почему

Раздел 4 спеки (`docs/superpowers/specs/2026-08-10-sql-code-completion-design.md`) фиксирует эти решения явно:

- **Отдельный lazy-endpoint колонок по одной таблице** — не вводился: при схемах до ~500 таблиц это лишний слой, весь снапшот (включая колонки) уходит на клиент целиком за один запрос `schema-map`.
- **FK-связи в самом schema-map** — не добавлялись: FK-сниппеты формирует сервер (`SemanticCompleter`) и делает это уже сейчас; дублировать эту логику на клиенте незачем.
- **Перенос семантики на клиент** (`schemaCompletionSource` из `@codemirror/lang-sql` вместо серверного ANTLR-движка) — отклонён: клиентский разбор контекста в `completion-schema.js` намеренно грубый (регулярные выражения на уровне текущего statement), точность на CTE и вложенных подзапросах остаётся только у серверного движка — отсюда и двухфазная схема, а не полная замена.
- **LSP/WebSocket-протокол, AST-парсер на клиенте, JS-тест-раннер** — не вводились; клиентский код этой итерации проверяется вручную в браузере, а не unit-тестами (в проекте нет JS test runner'а, вводить его для одной итерации избыточно).
- **`HybridCache` вместо существующего `MetadataCache`** — не менялось: `MetadataCache` уже даёт single-flight, TTL и stale-while-refresh; замена ради названия из исследования выше не имеет практического смысла.

### Ссылки

- Спека: `docs/superpowers/specs/2026-08-10-sql-code-completion-design.md`.
- План: `docs/superpowers/plans/2026-08-10-sql-completion-client-cache.md`.
- Ход выполнения и находки ревью батча 1: `.superpowers/sdd/progress.md`.
- Отчёты батча 2 — `docs/superpowers/reports/`: замеры интроспекции Oracle до/после `RESULT_CACHE` — `docs/superpowers/reports/2026-08-10-oracle-introspection-timing.md`; браузерная проверка — `docs/superpowers/reports/2026-08-10-completion-browser-verification.md`; ревью диффа — `docs/superpowers/reports/2026-08-10-completion-code-review.md`.