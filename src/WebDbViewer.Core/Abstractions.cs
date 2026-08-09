using System.Data.Common;

namespace WebDbViewer.Core;

/// <summary>Провайдер СУБД: фабрика соединений + интроспекция + генерация SQL под диалект.</summary>
public interface IDbProvider
{
    DbKind Kind { get; }

    /// <summary>
    /// true — у СУБД есть уровень «база данных» над схемами (PostgreSQL: одно подключение = одна база,
    /// список баз сервера доступен отдельно). false — корень дерева сразу схемы (Oracle).
    /// </summary>
    bool SupportsDatabaseLevel { get; }

    /// <summary>Строит строку подключения из конфигурации (пароль уже расшифрован).</summary>
    string BuildConnectionString(DataSourceConfig config, string plainPassword);

    /// <summary>Открывает новое соединение.</summary>
    Task<DbConnection> OpenConnectionAsync(DataSourceConfig config, string plainPassword, CancellationToken ct);

    /// <summary>Проверка подключения. Возвращает версию сервера.</summary>
    Task<string> TestConnectionAsync(DataSourceConfig config, string plainPassword, CancellationToken ct);

    /// <summary>Дочерние узлы дерева навигатора (lazy). parentPath: например ["public", "Tables"].</summary>
    Task<IReadOnlyList<DbObjectNode>> GetChildrenAsync(DbConnection connection, IReadOnlyList<string> parentPath, bool includeSystem, CancellationToken ct);

    /// <summary>Список схем.</summary>
    Task<IReadOnlyList<string>> GetSchemasAsync(DbConnection connection, bool includeSystem, CancellationToken ct);

    /// <summary>
    /// Базы данных сервера (для корня дерева). Пустой список — уровень БД не поддерживается
    /// (<see cref="SupportsDatabaseLevel"/> = false).
    /// </summary>
    Task<IReadOnlyList<DbObjectNode>> GetDatabasesAsync(DbConnection connection, bool includeSystem, CancellationToken ct);

    /// <summary>Полный снапшот схемы для кэша метаданных (таблицы, колонки, FK, роутины).</summary>
    Task<SchemaSnapshot> LoadSchemaSnapshotAsync(DbConnection connection, string schemaName, CancellationToken ct);

    /// <summary>Детали таблицы (для Data Editor).</summary>
    Task<TableInfo> GetTableInfoAsync(DbConnection connection, string schema, string table, CancellationToken ct);

    /// <summary>Версия/хэш DDL-состояния схемы для инвалидации кэша (LAST_DDL_TIME / pg event data).</summary>
    Task<string> GetSchemaVersionAsync(DbConnection connection, string schemaName, CancellationToken ct);

    /// <summary>Квотирование идентификатора по правилам диалекта (PG lowercase / Oracle UPPERCASE).</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>SELECT с keyset-пагинацией для просмотра данных таблицы.</summary>
    string BuildSelectPageSql(TableInfo table, DataPageRequest request);

    /// <summary>Псевдоколонка адреса строки для таблиц без PK (ctid / ROWID); null если нет.</summary>
    string? RowAddressPseudoColumn { get; }
}

/// <summary>Реестр провайдеров по DbKind.</summary>
public interface IDbProviderRegistry
{
    IDbProvider Get(DbKind kind);
}

/// <summary>Хранилище датасорсов (метабаза приложения).</summary>
public interface IDataSourceStore
{
    Task<IReadOnlyList<DataSourceConfig>> GetAllAsync(CancellationToken ct);
    Task<DataSourceConfig?> GetAsync(Guid id, CancellationToken ct);
    Task SaveAsync(DataSourceConfig config, CancellationToken ct);
    Task DeleteAsync(Guid id, CancellationToken ct);
}

/// <summary>Шифрование/дешифрование секретов (Data Protection API).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>
/// Stateful-сессия соединения пользователя с конкретным датасорсом:
/// держит открытое соединение (транзакции, temp tables, SET), TTL, ручной/авто commit.
/// </summary>
public interface IDbSession : IAsyncDisposable
{
    Guid SessionId { get; }
    Guid DataSourceId { get; }
    string UserName { get; }
    /// <summary>База данных соединения. Совпадает с базой из конфигурации, кроме сессий навигатора к другим базам сервера.</summary>
    string Database { get; }
    /// <summary>true — сессия к базе из конфигурации датасорса (основная); false — к другой базе сервера.</summary>
    bool IsPrimary { get; }
    DbConnection Connection { get; }
    bool AutoCommit { get; set; }
    bool InTransaction { get; }
    DateTimeOffset LastUsedAt { get; }

    Task BeginTransactionAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    Task RollbackAsync(CancellationToken ct);

    /// <summary>Отменяет текущую выполняющуюся команду.</summary>
    void CancelRunning();

    /// <summary>
    /// Занимает соединение сессии под одну команду. Физическое соединение обслуживает ровно одну
    /// команду за раз, а сессия общая для всех запросов пользователя к датасорсу — без этой
    /// сериализации два параллельных HTTP-запроса роняют друг друга («A command is already in progress»).
    /// Ожидание ограничено таймаутом: висеть до конца чужого получасового SELECT бессмысленно.
    /// </summary>
    /// <exception cref="DbSessionBusyException">Соединение не освободилось за отведённое время.</exception>
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct);
}

/// <summary>
/// Соединение сессии занято другой командой дольше отведённого времени ожидания.
/// Вызывающий переводит это в ответ «в сессии уже выполняется запрос», а не в 500.
/// </summary>
public sealed class DbSessionBusyException(string message) : InvalidOperationException(message);

/// <summary>
/// Открывает короткоживущие соединения к датасорсу вне пользовательской сессии.
/// Чтение метаданных (навигатор, DDL, описание таблицы) не нуждается в состоянии сессии,
/// а конкуренция за её единственное соединение — прямой источник сбоев и ожиданий.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Открывает соединение к датасорсу. <paramref name="database"/> — база сервера, отличная от базы
    /// из конфигурации (навигатор по всем базам); null — база датасорса.
    /// Вызывающий обязан освободить соединение (<c>await using</c>).
    /// </summary>
    Task<DbConnection> OpenAsync(Guid dataSourceId, string? database, CancellationToken ct);

    /// <summary>Вариант для уже загруженной конфигурации: не читает хранилище датасорсов повторно.</summary>
    Task<DbConnection> OpenAsync(DataSourceConfig config, string? database, CancellationToken ct);
}

/// <summary>Менеджер stateful-сессий: лимиты на пользователя, TTL, гарантированное освобождение.</summary>
public interface IDbSessionManager
{
    /// <summary>
    /// Сессия пользователя с датасорсом. <paramref name="database"/> — база сервера, отличная от базы
    /// из конфигурации (навигатор по всем базам); null — база датасорса.
    /// </summary>
    Task<IDbSession> GetOrCreateAsync(string userName, Guid dataSourceId, string? database, CancellationToken ct);
    Task<IDbSession?> FindAsync(Guid sessionId, CancellationToken ct);
    Task CloseAsync(Guid sessionId, CancellationToken ct);
    Task<IReadOnlyList<IDbSession>> ListForUserAsync(string userName, CancellationToken ct);
}

/// <summary>Кэш метаданных per-datasource: снапшоты схем, поиск, инвалидация.</summary>
public interface IMetadataCache
{
    /// <summary>Снапшот схемы: из кэша или загрузка (single-flight).</summary>
    Task<SchemaSnapshot> GetSchemaAsync(Guid dataSourceId, string schemaName, CancellationToken ct);

    /// <summary>Все закэшированные схемы датасорса.</summary>
    Task<IReadOnlyList<SchemaSnapshot>> GetLoadedSchemasAsync(Guid dataSourceId, CancellationToken ct);

    /// <summary>Фоновый прогрев (eager) списка схем.</summary>
    Task WarmupAsync(Guid dataSourceId, IReadOnlyList<string> schemas, CancellationToken ct);

    /// <summary>Префиксный/подстрочный поиск объектов (trie + camelCase матчинг).</summary>
    Task<IReadOnlyList<DbObjectNode>> SearchAsync(Guid dataSourceId, string query, int limit, CancellationToken ct);

    /// <summary>Инвалидация схемы (DDL-событие, ручной refresh, TTL).</summary>
    Task InvalidateAsync(Guid dataSourceId, string? schemaName, CancellationToken ct);
}

/// <summary>Разбиение скрипта на statements с учётом dollar-quoting, PL/SQL-блоков, слэш-терминатора.</summary>
public interface IStatementSplitter
{
    IReadOnlyList<SqlStatement> Split(string script, DbKind dialect);
}

public sealed record SqlStatement
{
    public required string Text { get; init; }
    /// <summary>Смещение начала statement в исходном скрипте (для маппинга ошибок).</summary>
    public int Offset { get; init; }
    public int Length { get; init; }
}

/// <summary>Кандидат автодополнения (LSP-подобная модель CompletionItem).</summary>
public sealed record CompletionItem
{
    public required string Label { get; init; }
    /// <summary>keyword | table | view | column | schema | function | alias | snippet.</summary>
    public required string Kind { get; init; }
    /// <summary>Текст вставки (с квотированием при необходимости).</summary>
    public string? InsertText { get; init; }
    public string? Detail { get; init; }
    public string? Documentation { get; init; }
    public int SortPriority { get; init; }
}

public sealed record CompletionRequest
{
    public required Guid DataSourceId { get; init; }
    public required string SqlText { get; init; }
    /// <summary>Позиция каретки — смещение в символах от начала текста.</summary>
    public required int CaretOffset { get; init; }
    public string? DefaultSchema { get; init; }
    public int Limit { get; init; } = 200;
}

/// <summary>Движок автодополнения (antlr4-c3 + кэш метаданных + семантическая модель).</summary>
public interface ICompletionEngine
{
    Task<IReadOnlyList<CompletionItem>> CompleteAsync(CompletionRequest request, DbKind dialect, CancellationToken ct);
}

/// <summary>Диагностика SQL (синтаксис + lint: DELETE/UPDATE без WHERE и т.п.).</summary>
public sealed record SqlDiagnostic
{
    public required string Message { get; init; }
    /// <summary>error | warning | info.</summary>
    public required string Severity { get; init; }
    public int Offset { get; init; }
    public int Length { get; init; }
    public string? Code { get; init; }
}

public interface ISqlDiagnosticsService
{
    Task<IReadOnlyList<SqlDiagnostic>> AnalyzeAsync(string sql, DbKind dialect, CancellationToken ct);
}

/// <summary>Аудит запросов (обязателен для гос-сектора).</summary>
public interface IQueryAuditor
{
    Task RecordAsync(AuditEntry entry, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> QueryAsync(DateTimeOffset from, DateTimeOffset to, string? userName, Guid? dataSourceId, int limit, CancellationToken ct);
}
