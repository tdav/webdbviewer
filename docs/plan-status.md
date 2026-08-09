# Состояние работ относительно PLAN.md

Дата оценки: 2026-08-08. Метод: сверка кода с планом, а не наличия файлов — для каждого пункта
проверялось, доступна ли функция во время выполнения (зарегистрирован ли сервис, замаплен ли
endpoint, отрисовывается ли UI).

Три состояния:

- **Работает** — код есть, подключён, доступен из приложения;
- **Не подключено** — код написан, но недостижим (endpoint не замаплен, сервис-заглушка, UI не отрисовывается);
- **Нет** — кода нет.

Базовые факты: `dotnet build` — 0 ошибок; unit-тесты — 219 прошли; интеграционные — 6 прошли
на живом PostgreSQL 18.3 (5 тестов метабазы плюс доставшаяся от шаблона пустая заглушка `UnitTest1`).

## Фаза 0 — каркас

| Пункт | Состояние | Комментарий |
|---|---|---|
| Solution и проекты | Работает | 8 проектов (`WebDbViewer.Storage.Postgres` добавлен при переносе настроек в БД) |
| Контракты Core | Работает | `IDbProvider`, `IDataSourceStore`, `IMetadataCache`, `ICompletionEngine`, `IQueryAuditor`, `IUserStore` |
| JS-инфраструктура (esbuild) | Работает | `frontend/build.mjs`, бандлы в `wwwroot/js` |
| Генерация ANTLR-парсеров | Работает | `grammars/`, `tools/antlr-4.13.2-complete.jar`, сгенерированный код закоммичен |

## Фаза MVP (батч №1) — практически закрыта

| # | Зона | Состояние | Комментарий |
|---|---|---|---|
| 1 | Подключения, пул, шифрование, сессии с TTL | Работает | `DbSessionManager`, `SessionSweeper`, транзакции/auto-commit, шифрование через Data Protection, проверка подключения из UI |
| 2 | Кэш метаданных | Работает | `MetadataCache` (single-flight, TTL, stale-while-revalidate), `PrefixTrie`, `HumpsMatcher`, интроспекция pg_catalog и ALL_* с фильтром OWNER; снапшоты теперь в PostgreSQL |
| 3 | HTMX UI | Работает | layout, навигатор с lazy `hx-get`, вкладки редактора, формы подключений, страница аудита, вход |
| 4 | Редактор и грид | Работает | CodeMirror 6 локально, виртуализованный грид, SSE-стриминг, отмена (`/api/query/cancel`), keyset-пагинация |
| 5 | Парсинг, автодополнение v0, аудит | Работает | ANTLR + antlr4-c3, `StatementSplitter` (dollar-quoting, PL/SQL), `CompletionEngine`, журнал аудита |

Проверено вживую: вход `admin/admin`, сохранение датасорса, тест подключения, выполнение
`SELECT 1` со стримингом, автодополнение таблиц, запись в журнал аудита.

## Фаза v1 (батч №2) — примерно наполовину

| # | Зона | Состояние | Что именно |
|---|---|---|---|
| 1 | Inline-редактирование данных | **Не подключено** | `PgDmlGenerator`/`OracleDmlGenerator` (ctid/ROWID) и unit-тесты есть, `Api/DataEditEndpoints.cs` есть, но `app.MapDataEditApi()` в `Program.cs` не вызывается, а `Pages/DataEditPanel.cshtml` ниоткуда не рендерится → функция недоступна |
| 2 | Семантическая модель автодополнения | Работает | `ScopeAnalyzer`, `QueryScopeModel`, алиасы, CTE, квотирование и регистр (`SqlIdentifierQuoting`) |
| 3 | DDL-генерация и экспорт | **Не подключено / частично** | `PgDdlGenerator`, `OracleDdlGenerator`, `PgDdlCatalogReader` написаны, но `app.MapDdlApi()` не вызывается. Экспорт: только `PgExportCopy` (CSV через COPY), без endpoint; JSON/XLSX и каталог `Web/Export` отсутствуют |
| 4 | Diagnostics/lint и инвалидация кэша | **Не подключено / частично** | `ISqlDiagnosticsService` зарегистрирован как `NoopSqlDiagnosticsService` — линта (DELETE/UPDATE без WHERE) нет. Инвалидация: TTL и `GetSchemaVersionAsync` (Oracle `LAST_DDL_TIME`) работают; LISTEN/NOTIFY и event triggers не реализованы |
| 5 | Мониторинг сессий, EXPLAIN, навигация по FK | **Нет** | `pg_stat_activity`/`v$session`, EXPLAIN, переходы по FK и referencing tables отсутствуют |

## Фаза v2 (батч №3) — не начата

ER-диаграммы, schema diff, визуализация плана, LSP-подобный WS-протокол (hover, signatureHelp,
documentSymbol), генерация тестовых данных и value completion — кода нет
(`wwwroot/js/erd`, `wwwroot/js/plan`, `Api/Language` отсутствуют).

## Фаза verify (батч №4) — частично

| Пункт | Состояние | Комментарий |
|---|---|---|
| `dotnet build` | Работает | 0 ошибок; SQLite-зависимость снята, NU1903 больше нет |
| `dotnet test` (unit) | Работает | 214 тестов (тесты SQLite-хранилища удалены вместе с ним) |
| Интеграционные тесты на живом PostgreSQL | Частично | Был только пустой `UnitTest1`; добавлены 5 тестов метабазы. Провайдер, endpoints и UI интеграционно не покрыты |
| Параллельный code-review | Нет | Не проводился |
| Документация (рус.) | Частично | Появились `docs/metastore-postgres.md` и этот файл; README, руководство пользователя и ADR из плана отсутствуют |
| Чек-лист развёртывания | Нет | — |
| CI | Нет | `.github/workflows` пуст |
| Финальный пуш в `tdav/webdbviewer` | Нет | Один локальный коммит, remote не настроен |

## Логирование (сверх плана)

PLAN.md логирование не оговаривает. Подключён Serilog (двухэтапная инициализация, Console + файлы с
ротацией, одна сводная запись на HTTP-запрос). Подробности: `docs/logging-serilog.md`.

## Хранение настроек (сверх плана)

План предполагал файловую метабазу (JSON + SQLite-снапшот + `audit.db` + папка ключей). Сейчас все
настройки — датасорсы, учётные записи, ключи Data Protection, снапшоты метаданных и журнал аудита —
в одной базе PostgreSQL (`dbviewer_db`, схема `webdbviewer`). Подробности: `docs/metastore-postgres.md`.

## Что взять следующим шагом

1. Подключить уже написанный код: `MapDataEditApi()`, `MapDdlApi()` и отрисовку `DataEditPanel` —
   это дешевле любой новой функции и закрывает два пункта v1.
2. Реализовать `ISqlDiagnosticsService` вместо заглушки (lint DELETE/UPDATE без WHERE из плана).
3. Экспорт: endpoint поверх `PgExportCopy` плюс JSON/XLSX.
4. Удалить оставшееся неиспользуемое файловое хранилище `DataSourceFileStore`.
5. Наполнить `.github/workflows` (build + test) и написать README с руководством.
