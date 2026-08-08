# План реализации WebDbViewer

Веб-клиент БД (по образцу DBeaver/CloudBeaver) и SQL IntelliSense на **ASP.NET Core (.NET 10) + HTMX**.
Основание: технический отчёт «Web-Based DB Viewer and SQL IntelliSense on ASP.NET Core 10».

## Ключевые решения (из отчёта)

- **Стек**: ASP.NET Core (.NET 10), Razor Pages/MVC + HTMX; JS-острова: **CodeMirror 6** (SQL-редактор), виртуализованный result grid (собственный), SSE для стриминга/прогресса.
- **СУБД**: PostgreSQL (Npgsql) + Oracle (Oracle.ManagedDataAccess.Core), провайдерная архитектура `IDbProvider`.
- **Парсинг SQL**: ANTLR4 (`Antlr4.Runtime.Standard`, грамматики grammars-v4 PostgreSQL + PL/SQL) + **antlr4-c3** (C#-порт, исходники в проекте) для автодополнения; сгенерированный парсер коммитится в репозиторий (air-gapped).
- **Кэш метаданных**: pg_catalog / ALL_* (фильтры по OWNER), per-datasource, in-memory + persistent snapshot (SQLite), single-flight, инвалидация (LISTEN/NOTIFY, LAST_DDL_TIME, TTL).
- **Сессии БД**: stateful-менеджер соединений с TTL, привязка к пользовательской сессии (транзакции, temp tables, manual commit).
- **Безопасность**: шифрование credentials через Data Protection API, read-only флаги, аудит запросов (must для гос-сектора).
- **Язык UI и документации**: русский.

## Используемые skills (по фазам)

| Skill | Где применяется |
|---|---|
| `engineering:architecture` | ADR по ключевым решениям (CodeMirror vs Monaco, ANTLR vs libpg_query, LSP vs HTTP) — фаза 0 |
| `engineering:system-design` | Проектирование границ сервисов, модель данных, контракты `IDbProvider` — фаза 0 |
| `engineering:testing-strategy` | Стратегия тестов: unit (completion-корпус с маркером каретки), интеграционные на живом PostgreSQL — фазы MVP/verify |
| `engineering:code-review` | Параллельное ревью агентами перед финальным пушем — фаза verify |
| `engineering:documentation` | README.md (рус.), руководство, runbook — фаза verify |
| `engineering:deploy-checklist` | Чек-лист перед финальным пушем — фаза verify |
| `data:sql-queries` / `data:write-query` | Интроспекционные запросы pg_catalog / ALL_*, keyset-пагинация |
| `dataviz` | Визуализация EXPLAIN-плана и ER-диаграммы (v2) |
| `mcp__Context7__*` | Актуальная документация Npgsql, CodeMirror 6, HTMX, ANTLR при реализации |

## Структура решения

```
WebDbViewer.sln
src/
  WebDbViewer.Core/            — модели, контракты (IDbProvider, IMetadataCache, ISessionManager, ICompletionEngine)
  WebDbViewer.Providers.Postgres/  — Npgsql: интроспекция pg_catalog, COPY-экспорт, LISTEN/NOTIFY
  WebDbViewer.Providers.Oracle/    — ODP.NET Core: ALL_*/USER_*, ROWID, LAST_DDL_TIME
  WebDbViewer.Metadata/        — кэш метаданных, trie-поиск, инвалидация, SQLite-снапшот
  WebDbViewer.Parsing/         — ANTLR-парсеры (сгенерированные), antlr4-c3, split statements, lint
  WebDbViewer.Completion/      — движок автодополнения, семантическая модель (алиасы, CTE)
  WebDbViewer.Web/             — ASP.NET Core: HTMX UI, SSE, endpoints, аудит, Data Protection
    wwwroot/js/                — CodeMirror 6 бандл (локально), грид, htmx
tests/
  WebDbViewer.Tests.Unit/
  WebDbViewer.Tests.Integration/   — живой PostgreSQL
grammars/                      — .g4 исходники + скрипт генерации (Java)
docs/                          — ADR, руководство (рус.)
```

## Фазы и параллельные агенты (батчи по 5)

### Фаза 0 — каркас (основная сессия, без агентов)
Solution, контракты Core, конфигурация сборки, JS-инфраструктура (esbuild), генерация ANTLR-парсеров, первый коммит.

### Фаза MVP — батч №1 (5 агентов параллельно)
| # | Агент | Зона (директории) |
|---|---|---|
| 1 | Подключения: датасорсы, пул, шифрование (Data Protection), тест подключения, stateful-менеджер сессий с TTL, транзакции/auto-commit | Providers.*/Connection*, Core/Sessions |
| 2 | Кэш метаданных: интроспекция pg_catalog + ALL_* (фильтры OWNER), trie, single-flight, SQLite-снапшот | Metadata/ |
| 3 | HTMX UI: layout, Database Navigator (lazy hx-get), вкладки редактора, формы подключений, страницы аудита | Web/ (Pages, Components) |
| 4 | SQL-редактор + Result Grid: CodeMirror 6, виртуализованный грид, SSE-стриминг строк, отмена/таймаут, keyset-пагинация | Web/wwwroot/js, Web/Api |
| 5 | Парсинг: ANTLR-инфраструктура, antlr4-c3, split statements (dollar-quoting, PL/SQL слэш), автодополнение v0 (keywords+таблицы+колонки), аудит запросов | Parsing/, Completion/, Web/Audit |

### Фаза v1 — батч №2 (5 агентов параллельно)
| # | Агент | Зона |
|---|---|---|
| 1 | Inline-редактирование данных: UPDATE/INSERT/DELETE по PK, ctid/ROWID, cell editor | Web/, Providers.* |
| 2 | Семантическая модель автодополнения: алиасы, `a.`, CTE, JOIN ON по FK, автоалиасы, quoting/регистр | Completion/ |
| 3 | DDL-генерация + экспорт CSV/JSON/XLSX (стриминг, COPY) | Providers.*, Web/Export |
| 4 | Diagnostics/lint (DELETE/UPDATE без WHERE), инвалидация кэша (event triggers + LISTEN/NOTIFY, LAST_DDL_TIME) | Parsing/, Metadata/ |
| 5 | Мониторинг сессий (pg_stat_activity, v$session), EXPLAIN (текст), навигация по FK/referencing tables | Web/, Providers.* |

### Фаза v2 — батч №3 (5 агентов параллельно)
| # | Агент | Зона |
|---|---|---|
| 1 | ER-диаграммы (SVG, автолейаут из FK) | Web/wwwroot/js/erd |
| 2 | Schema diff | Core/, Web/ |
| 3 | Визуализация EXPLAIN-плана (JSON → дерево) | Web/wwwroot/js/plan |
| 4 | LSP-подобный WS-протокол: hover, signatureHelp, documentSymbol | Web/Api/Language |
| 5 | Генерация тестовых данных + value completion | Providers.*, Completion/ |

### Фаза verify — батч №4 (агенты ревью + исправления)
Сборка `dotnet build`, `dotnet test`, интеграционные тесты на живом PostgreSQL 16, параллельный code-review (корректность, безопасность/SQL-injection, производительность), исправления, документация (рус.), финальный пуш в `tdav/webdbviewer`.

## Риски (учтены из отчёта)
Все JS-библиотеки хостятся локально (air-gapped); сгенерированные ANTLR-парсеры коммитятся; ALL_* только с фильтрами по OWNER; регистр идентификаторов (PG lowercase / Oracle UPPERCASE) учитывается в кэше и квотировании; ODP.NET pooling — лимиты на пользователя; Oracle недоступен в песочнице — покрытие юнит-тестами, интеграция — PostgreSQL.
