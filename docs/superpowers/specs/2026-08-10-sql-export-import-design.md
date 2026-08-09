# Экспорт и импорт структуры и данных в формате SQL

Дата: 2026-08-10. Диалекты: PostgreSQL и Oracle.

## Задача

Выгрузить таблицу (структуру, данные или и то и другое) в самодостаточный `.sql`-скрипт
и залить такой скрипт обратно — в ту же или другую базу, того же или другого диалекта
в пределах поддерживаемых двух.

## Что переиспользуется

| Нужно | Уже есть |
|---|---|
| CREATE TABLE + ограничения + индексы + комментарии | `IDdlGenerator.GetTableDdlAsync` (`PgDdlGenerator`, `OracleDdlGenerator`) |
| Разбиение скрипта на statements (dollar-quoting, PL/SQL, слэш) | `IStatementSplitter` |
| Квотирование идентификаторов по правилам диалекта | `IDbProvider.QuoteIdentifier` |
| Открытое соединение пользователя, транзакции | `IDbSessionManager`, `IDbSession` |
| Журналирование выполненного SQL | `IQueryAuditor` |

Нового кода: форматирование значений в SQL-литералы, писатель INSERT-скрипта,
три endpoint'а, обработчик вкладки редактора и разметка UI.

## Компоненты

### `WebDbViewer.Core/Export/SqlScriptExport.cs`

Один файл, без интерфейса и DI-регистрации: диалектных различий примерно тридцать строк,
и они локализованы в `SqlLiteral`. (`IDdlGenerator`/`IDmlGenerator` разделены на реализации
потому, что там принципиально разный код работы с каталогом БД; здесь этого нет.)

**`SqlLiteral.Format(object? value, DbKind kind)`** — значение из `DbDataReader` → текст литерала:

| Тип | PostgreSQL | Oracle |
|---|---|---|
| `null` / `DBNull` | `NULL` | `NULL` |
| `string` | `'…'`, одинарная кавычка удваивается | то же |
| целые, `decimal`, `float`, `double` | инвариантный формат, без кавычек | то же |
| `bool` | `true` / `false` | `1` / `0` |
| `DateTime` | `TIMESTAMP '2024-01-15 10:30:00.123'` | `TO_TIMESTAMP('…','YYYY-MM-DD HH24:MI:SS.FF3')` |
| `DateTimeOffset` | `TIMESTAMP WITH TIME ZONE '…'` | `TO_TIMESTAMP_TZ('…','…TZH:TZM')` |
| `TimeSpan` | `INTERVAL '…'` | `INTERVAL '…' DAY TO SECOND` |
| `Guid` | `'…'::uuid` | `'…'` (Oracle хранит как строку/RAW) |
| `byte[]` | `'\x0a0b'::bytea` | `HEXTORAW('0A0B')` |
| прочее | строкой в кавычках | то же |

Ограничение Oracle: литерал `HEXTORAW` принимает не более 2000 байт. Значение крупнее
не обрезается молча — писатель выводит `NULL` и комментарий с именем колонки и размером,
чтобы потеря данных была видна в скрипте.

**`InsertScriptWriter`** — стримит `INSERT` из открытого `DbDataReader` в `TextWriter`:
одна строка данных = один `INSERT INTO target (cols) VALUES (…);`. Формат совместим
с обоими диалектами (Oracle не поддерживает многострочный `VALUES`) и переживает
построчное разбиение сплиттером при импорте. Батчинг — сознательно нет: добавим,
если упрёмся в скорость импорта.

Возвращает число записанных строк. Память не держит — пишет по мере чтения курсора.

### Экспорт: `WebDbViewer.Web/Api/ExportEndpoints.cs`

- `GET /api/export/sql?ds&schema&table&structure&data&db&limit` — стрим `.sql` через
  `Content-Disposition: attachment`. `structure=true` добавляет DDL из `IDdlGenerator`,
  `data=true` — INSERT'ы. Хотя бы один из флагов обязателен.
- `POST /api/export/sql/query` — тело `{ ds, sql, target, db }`: результат произвольного
  SELECT в INSERT'ы указанной таблицы `target`. Структуру взять неоткуда — только данные.
- `OnGetExportTabAsync` в `Pages/Editor/Index.cshtml.cs` — тот же скрипт во вкладку
  редактора, с жёстким лимитом строк (`ExportLimits.EditorRowLimit`): CodeMirror не держит
  скрипт на миллион строк. При достижении лимита в конец пишется комментарий.

### Импорт: `WebDbViewer.Web/Api/ImportEndpoints.cs`

`POST /api/import/sql`, multipart: файл + `ds` + `db` + режим.

- Режим `transaction` (по умолчанию) — весь скрипт в одной транзакции, ошибка → rollback.
- Режим `continue` — по statement, ошибки собираются в отчёт, успешные остаются.
- Датасорс с `ReadOnly = true` — отказ до первого выполнения.
- Каждый запуск пишется в `IQueryAuditor` (одна запись на импорт, с числом statements).
- Ответ — JSON: выполнено statements, затронуто строк, список ошибок с номером statement.

Второй режим импорта — «в редактор»: `OnPostImportTabAsync` кладёт содержимое файла
в новую вкладку без выполнения (лимит по размеру файла).

### UI

- `Pages/Shared/_TreeNode.cshtml` — кнопка экспорта у узлов Table/View/MaterializedView
  рядом с DDL и DROP; открывает вкладку редактора со скриптом.
- Тулбар редактора — кнопка импорта (форма с выбором файла и режима), скрыта, если
  датасорс `ReadOnly`.

## Тесты

Паттерн проекта: `SkippableFact`, строка подключения из переменной окружения,
недоступный сервер → тест пропускается.

**Unit** (`tests/WebDbViewer.Tests.Unit/Export/`):
- `SqlLiteralTests` — все строки таблицы типов выше, оба диалекта: NULL, удвоение кавычки,
  инвариантный формат чисел (не зависит от культуры), даты, `byte[]`, `bool`, перевод строки.
- `InsertScriptWriterTests` — форма скрипта на фейковом `DbDataReader`: квотирование
  идентификаторов по диалекту, порядок колонок, терминатор, счётчик строк,
  поведение при превышении лимита Oracle на `HEXTORAW`.

**Integration PostgreSQL** (`WEBDBVIEWER_TEST_DEMO_DB`):
round-trip — экспорт таблицы демо-базы (структура + данные) → создание копии из скрипта
во временной схеме → сверка числа строк и содержимого → удаление схемы.

**Integration Oracle** (`WEBDBVIEWER_TEST_ORACLE`):
то же самое; интеграционный проект получает `ProjectReference` на `WebDbViewer.Providers.Oracle`.

## Вне объёма

- Экспорт схемы целиком и топологическая сортировка таблиц по FK — экспортируется
  одна таблица за раз.
- Конвертация типов между диалектами при импорте: скрипт, снятый с Oracle, применяется
  к Oracle. Кросс-диалектный перенос данных (INSERT'ы) работает, кросс-диалектный
  перенос структуры (DDL) — нет.
- Форматы, отличные от SQL (CSV уже есть отдельно в `PgExportCopy`, JSON/XLSX не трогаем).
