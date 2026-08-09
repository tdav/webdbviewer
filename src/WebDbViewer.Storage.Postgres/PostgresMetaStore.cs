using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace WebDbViewer.Storage.Postgres;

/// <summary>
/// Метабаза приложения на PostgreSQL: единый пул соединений (<see cref="NpgsqlDataSource"/>),
/// проверенное имя схемы и ленивая (single-flight) инициализация DDL.
/// Схема создаётся кодом через CREATE TABLE IF NOT EXISTS — без EF Core и файлов миграций,
/// по тому же принципу, что и прежние SQLite-хранилища.
/// </summary>
public sealed class PostgresMetaStore : IAsyncDisposable
{
    private static readonly Regex SafeSchemaName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private readonly NpgsqlDataSource dataSource;
    private readonly string connectionString;
    private readonly SemaphoreSlim initLock = new(1, 1);
    private volatile bool initialized;

    public PostgresMetaStore(IOptions<PostgresStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        if (string.IsNullOrWhiteSpace(value.ConnectionString))
            throw new InvalidOperationException(
                "Не задана строка подключения к метабазе (ConnectionStrings:MetaStore).");

        if (!SafeSchemaName.IsMatch(value.Schema))
            throw new InvalidOperationException($"Недопустимое имя схемы метабазы: «{value.Schema}».");

        Schema = value.Schema;
        connectionString = value.ConnectionString;
        dataSource = NpgsqlDataSource.Create(value.ConnectionString);
    }

    /// <summary>Имя схемы метабазы (проверено на безопасность подстановки в SQL).</summary>
    public string Schema { get; }

    /// <summary>Открывает соединение; при первом обращении создаёт схему и таблицы.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        if (!initialized)
            await EnsureCreatedAsync(ct).ConfigureAwait(false);

        return await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Создаёт схему и все служебные таблицы, если их ещё нет (идемпотентно).</summary>
    public async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (initialized)
            return;

        await initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (initialized)
                return;

            NpgsqlConnection connection;
            try
            {
                connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidCatalogName)
            {
                await CreateDatabaseAsync(ct).ConfigureAwait(false);
                connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
            }

            await using (connection)
            {
                await using var cmd = new NpgsqlCommand(BuildDdl(Schema), connection);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            initialized = true;
        }
        finally
        {
            initLock.Release();
        }
    }

    /// <summary>
    /// Создаёт саму базу метахранилища, если её ещё нет: подключается к служебной базе
    /// <c>postgres</c> тем же пользователем и выполняет CREATE DATABASE.
    /// </summary>
    private async Task CreateDatabaseAsync(CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var database = builder.Database
            ?? throw new InvalidOperationException("В строке подключения к метабазе не указана база данных.");

        builder.Database = "postgres";
        builder.Pooling = false;

        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync(ct).ConfigureAwait(false);

        var quoted = "\"" + database.Replace("\"", "\"\"") + "\"";
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {quoted}", admin);
        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
        {
            // База появилась параллельно (другой экземпляр приложения) — этого и добивались.
        }
    }

    /// <summary>Полная схема метабазы: датасорсы, пользователи, ключи Data Protection, снапшоты, аудит.</summary>
    private static string BuildDdl(string schema) => $"""
        CREATE SCHEMA IF NOT EXISTS {schema};

        CREATE TABLE IF NOT EXISTS {schema}.datasources (
            id                        uuid        PRIMARY KEY,
            name                      text        NOT NULL,
            kind                      text        NOT NULL,
            host                      text        NOT NULL,
            port                      integer     NOT NULL,
            database                  text        NOT NULL,
            username                  text        NOT NULL,
            protected_password        text        NULL,
            read_only                 boolean     NOT NULL DEFAULT false,
            is_production             boolean     NOT NULL DEFAULT false,
            use_ssl                   boolean     NOT NULL DEFAULT false,
            connect_timeout_seconds   integer     NOT NULL DEFAULT 15,
            command_timeout_seconds   integer     NOT NULL DEFAULT 120,
            max_pool_size_per_user    integer     NOT NULL DEFAULT 5,
            extra                     jsonb       NULL,
            updated_at                timestamptz NOT NULL DEFAULT now()
        );

        -- Добавлено позже: у существующих датасорсов сохраняем прежнее поведение (все схемы видимы).
        ALTER TABLE {schema}.datasources
            ADD COLUMN IF NOT EXISTS allow_all_schemas boolean NOT NULL DEFAULT true;

        -- username хранится нормализованным (нижний регистр): вход нечувствителен к регистру.
        CREATE TABLE IF NOT EXISTS {schema}.users (
            username             text        PRIMARY KEY,
            password_hash        text        NOT NULL,
            role                 text        NOT NULL DEFAULT 'admin',
            must_change_password boolean     NOT NULL DEFAULT false,
            created_at           timestamptz NOT NULL DEFAULT now(),
            updated_at           timestamptz NOT NULL DEFAULT now()
        );

        CREATE TABLE IF NOT EXISTS {schema}.data_protection_keys (
            friendly_name text        PRIMARY KEY,
            xml           text        NOT NULL,
            created_at    timestamptz NOT NULL DEFAULT now()
        );

        -- schema_key = lower(schema_name): регистронезависимый ключ (PG отдаёт схемы в нижнем
        -- регистре, Oracle — в верхнем), при этом исходное написание сохраняется в schema_name.
        CREATE TABLE IF NOT EXISTS {schema}.schema_snapshots (
            datasource_id uuid        NOT NULL,
            schema_key    text        NOT NULL,
            schema_name   text        NOT NULL,
            json          jsonb       NOT NULL,
            version_hash  text        NULL,
            loaded_at     timestamptz NOT NULL,
            PRIMARY KEY (datasource_id, schema_key)
        );

        CREATE TABLE IF NOT EXISTS {schema}.audit_entries (
            id             uuid        PRIMARY KEY,
            user_name      text        NOT NULL,
            data_source_id uuid        NOT NULL,
            sql_text       text        NOT NULL,
            started_at     timestamptz NOT NULL,
            duration_ms    bigint      NOT NULL,
            rows_affected  bigint      NULL,
            success        boolean     NOT NULL,
            error_message  text        NULL,
            client_ip      text        NULL
        );

        CREATE INDEX IF NOT EXISTS ix_audit_started_at ON {schema}.audit_entries (started_at DESC);
        CREATE INDEX IF NOT EXISTS ix_audit_user       ON {schema}.audit_entries (user_name, started_at DESC);
        CREATE INDEX IF NOT EXISTS ix_audit_ds         ON {schema}.audit_entries (data_source_id, started_at DESC);
        """;

    public async ValueTask DisposeAsync()
    {
        await dataSource.DisposeAsync().ConfigureAwait(false);
        initLock.Dispose();
    }
}
