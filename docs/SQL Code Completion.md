# План: удобный, отзывчивый и эффективный SQL Code Completion для PostgreSQL и Oracle в webdbviewer

## TL;DR
- **Рекомендуется гибридная архитектура (Вариант C):** список таблиц/вью грузится один раз при открытии редактора в серверный `HybridCache` и отдаётся на клиент как JSON schema-map для Monaco/CodeMirror provider (фильтрация префикса на клиенте, p95 < 50 мс); колонки конкретной таблицы догружаются **lazy** по `alias.`/`FROM` через отдельный JSON endpoint. Это ровно тот путь, который прошли pgAdmin 4 (уход на клиентский CodeMirror-autocomplete) и DataGrip (introspection levels + on-demand догрузка деталей).
- **Лечение «перегруза» Oracle:** отказ от загрузки всех колонок всех схем сразу; переход на `USER_*`/фильтр по `OWNER` (не `DBA_*`), хинт `RESULT_CACHE`, серверный кэш и lazy-колонки. Причина медлительности подтверждена JetBrains: «Oracle catalogs are generally pretty slow» и сложная проверка прав на каждый объект в `ALL_*`.
- **Ключевые числа:** debounce до попапа 150–250 мс, client interaction delay ~75 мс, max рендер 50 предложений, TTL кэша таблиц 10–30 мин / колонок 30–60 мин + ручной Refresh; цель p95 lazy-запроса < 500 мс (тёплый кэш), время до первого попапа < 1 с.

---

## Диагностика текущего состояния

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