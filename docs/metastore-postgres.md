# Метабаза приложения (PostgreSQL)

Все настройки WebDbViewer хранятся в одной локальной базе PostgreSQL. Файловых хранилищ
(`App_Data/datasources.json`, `metadata.db`, `audit.db`, папка `keys/`) в рабочем контуре больше нет.

## Строка подключения

`src/WebDbViewer.Web/appsettings.json`:

```json
"ConnectionStrings": {
  "MetaStore": "Host=localhost;Port=5432;Database=dbviewer_db;Username=postgres;Password=1;Pooling=true;"
},
"MetaStore": { "Schema": "webdbviewer" }
```

Строка подключения к самой метабазе — единственная настройка, которая по определению не может
храниться в метабазе. Для развёртывания её следует передавать переменной окружения
`ConnectionStrings__MetaStore`, а не держать в файле.

Базу нужно создать заранее (`CREATE DATABASE dbviewer_db;`); схему и таблицы приложение создаёт само
при старте — идемпотентным `CREATE ... IF NOT EXISTS`, без EF Core и файлов миграций
(тот же подход, что был у прежних SQLite-хранилищ).

## Что где лежит

| Таблица (`webdbviewer.*`) | Содержимое | Интерфейс |
|---|---|---|
| `datasources` | конфигурации подключений; пароль — только в зашифрованном виде | `IDataSourceStore` |
| `users` | учётные записи UI: имя (нижний регистр), PBKDF2-хэш пароля, роль | `IUserStore` |
| `data_protection_keys` | кольцо ключей Data Protection | `IXmlRepository` |
| `schema_snapshots` | persistent-снапшоты схем для кэша метаданных (`jsonb`) | `ISnapshotStore` |
| `audit_entries` | журнал выполненных запросов | `IQueryAuditor` |

Реализации — в проекте `src/WebDbViewer.Storage.Postgres`, регистрация — `AddPostgresMetaStore(...)`
в `Program.cs`. Вызывается **до** `AddDbSessions`/`AddMetadataCache`: те регистрируют файловые
реализации через `TryAdd` и не перезаписывают уже зарегистрированные.

### Почему ключи Data Protection обязаны быть в базе

Пароли датасорсов шифруются Data Protection. Если кольцо ключей осталось бы на диске, а конфигурации
переехали в базу, то на любой другой машине пароли перестали бы расшифровываться. Поэтому ключи
переехали вместе с настройками; при старте выполняется одноразовый импорт существующей папки
`src/WebDbViewer.Web/keys` (уже импортированные ключи повторно не перезаписываются).

Ключи лежат в базе в виде XML открытым текстом — доступ к метабазе равнозначен доступу к паролям
датасорсов. Для продуктивного контура нужно ограничить права на схему `webdbviewer` отдельной ролью
и/или добавить `ProtectKeysWithDpapi()`/сертификат (последнее привязывает ключи к машине).

## Первый пароль администратора

Учётные записи хранятся в `webdbviewer.users`. Если таблица пуста, `AdminSeeder` создаёт первого
администратора; пароль определяется в следующем порядке:

1. `Auth:InitialPassword` — пароль открытым текстом (задавайте через переменную окружения
   `Auth__InitialPassword` или user-secrets, а не в файле);
2. `Auth:PasswordHash` — готовый PBKDF2-хэш; позволяет перенести ранее настроенную учётку;
3. значение по умолчанию **`admin`** — помечается флагом `must_change_password`, при старте и при
   каждом входе пишется предупреждение в журнал.

В текущей поставке `Auth:PasswordHash` в `appsettings.json` соответствует паролю **`admin`**,
поэтому на чистой базе первый вход — **`admin` / `admin`**.

Смена пароля (UI смены пароля пока нет):

```bash
psql -h localhost -U postgres -d dbviewer_db -c "UPDATE webdbviewer.users SET password_hash = 'PBKDF2-SHA256$...', must_change_password = false WHERE username = 'admin';"
```

Хэш вычисляется методом `WebDbViewer.Web.Security.PasswordHasher.Hash(пароль)`.

## Проверка

Интеграционные тесты метабазы: `tests/WebDbViewer.Tests.Integration/MetaStore/PostgresMetaStoreTests.cs`.
Каждый запуск работает во временной схеме, которая удаляется после тестов. Строку подключения можно
переопределить переменной `WEBDBVIEWER_TEST_METASTORE`; если сервер недоступен, тесты пропускаются.

```bash
dotnet test tests/WebDbViewer.Tests.Integration/WebDbViewer.Tests.Integration.csproj
```

## Что осталось от файлового варианта

SQLite-реализации (`SqliteSnapshotStore`, `SqliteQueryAuditor`), их DI-расширения и unit-тесты
удалены вместе с пакетом `Microsoft.Data.Sqlite` — он тянул `SQLitePCLRaw.lib.e_sqlite3` 2.1.11
с известной уязвимостью NU1903. Из файлового варианта остаётся `DataSourceFileStore`:
класс сохранён, но в `Program.cs` не регистрируется.
