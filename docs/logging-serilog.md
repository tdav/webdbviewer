# Логирование (Serilog)

Приложение использует Serilog как единственный провайдер логирования (пакет `Serilog.AspNetCore`).

## Схема инициализации

В [Program.cs](../src/WebDbViewer.Web/Program.cs) используется двухэтапная инициализация:

1. **Bootstrap-логгер** (`CreateBootstrapLogger`) создаётся до `WebApplication.CreateBuilder` — он
   ловит ошибки старта, которые иначе потерялись бы: недоступная метабаза, битая строка подключения,
   сбой создания схемы.
2. **Полный логгер** регистрируется через `builder.Services.AddSerilog(...)` с
   `ReadFrom.Configuration(...)` и `ReadFrom.Services(...)` — читает секцию `Serilog` из appsettings
   и подхватывает сервисы DI.

Весь запуск обёрнут в `try/catch/finally`: фатальная ошибка пишется как `Log.Fatal`, а
`Log.CloseAndFlushAsync()` в `finally` гарантирует, что буферы sink'ов сброшены.

## Приёмники

| Приёмник | Куда | Настройки |
|---|---|---|
| Console | stdout | краткий шаблон `[HH:mm:ss LVL] сообщение` |
| File | `src/WebDbViewer.Web/logs/webdbviewer-<дата>.log` | ротация по дню, 30 файлов, лимит 100 МБ на файл, полный шаблон со `SourceContext` и свойствами |

Каталог `logs/` добавлен в `.gitignore`.

Уровни задаются в секции `Serilog:MinimumLevel` (`appsettings.json` — Information,
`appsettings.Development.json` — Debug). Секции `Logging:LogLevel` больше нет: после `AddSerilog()`
штатные провайдеры логирования не используются, и она вводила бы в заблуждение.

## Логирование запросов

`UseSerilogRequestLogging` подключён сразу после `UseStaticFiles` — одна сводная запись на запрос
вместо нескольких событий ASP.NET Core, при этом обращения к css/js в журнал не попадают.

- шаблон: `HTTP {RequestMethod} {RequestPath} → {StatusCode} за {Elapsed:0.0} мс`;
- уровень: `Error` при исключении или 5xx, `Warning` при 4xx, иначе `Information`;
- к каждой записи добавляются `User` (имя из claims либо `anonymous`) и `ClientIp`.

Тело запроса не логируется — форма входа и пароли датасорсов в журнал не попадают.

## Соглашения при написании кода

- Только шаблоны сообщений с именованными свойствами: `logger.LogInformation("Сохранено подключение «{Name}» ({Id})", name, id)`.
  Интерполяция строк ломает структурность и выделяет память даже при отключённом уровне.
- Не логировать пароли, токены и содержимое ячеек данных.
- Для сквозных свойств использовать `LogContext.PushProperty(...)` — обогатитель `FromLogContext` включён.

## Возможные следующие шаги

- Отдельный приёмник в саму метабазу (`Serilog.Sinks.PostgreSQL`), если журнал приложения должен
  лежать рядом с журналом аудита запросов. Сейчас это разные вещи: аудит — таблица
  `webdbviewer.audit_entries` (кто какой SQL выполнил), логи Serilog — консоль и файлы.
- `Serilog.Sinks.Seq` или OTLP-приёмник для централизованного сбора.
