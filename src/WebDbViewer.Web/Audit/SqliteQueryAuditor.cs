using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Audit;

/// <summary>
/// Аудит выполненных запросов в отдельной базе SQLite (audit.db).
/// Обязателен для гос-сектора: кто, когда, на каком датасорсе, какой SQL, результат.
/// </summary>
public sealed class SqliteQueryAuditor : IQueryAuditor
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteQueryAuditor(IOptions<AuditOptions> options)
    {
        var path = options.Value.DbPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_entries
                (id, user_name, data_source_id, sql_text, started_at, duration_ms,
                 rows_affected, success, error_message, client_ip)
            VALUES
                ($id, $user, $ds, $sql, $started, $duration,
                 $rows, $success, $error, $ip);
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id.ToString("N"));
        cmd.Parameters.AddWithValue("$user", entry.UserName);
        cmd.Parameters.AddWithValue("$ds", entry.DataSourceId.ToString("N"));
        cmd.Parameters.AddWithValue("$sql", entry.SqlText);
        cmd.Parameters.AddWithValue("$started", entry.StartedAt.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$duration", (long)entry.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$rows", (object?)entry.RowsAffected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$success", entry.Success ? 1 : 0);
        cmd.Parameters.AddWithValue("$error", (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ip", (object?)entry.ClientIp ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(
        DateTimeOffset from, DateTimeOffset to, string? userName, Guid? dataSourceId,
        int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var cmd = connection.CreateCommand();

        var where = "started_at >= $from AND started_at <= $to";
        cmd.Parameters.AddWithValue("$from", from.UtcDateTime.ToString("O"));
        cmd.Parameters.AddWithValue("$to", to.UtcDateTime.ToString("O"));
        if (!string.IsNullOrWhiteSpace(userName))
        {
            where += " AND user_name = $user";
            cmd.Parameters.AddWithValue("$user", userName);
        }
        if (dataSourceId is { } ds)
        {
            where += " AND data_source_id = $ds";
            cmd.Parameters.AddWithValue("$ds", ds.ToString("N"));
        }
        cmd.Parameters.AddWithValue("$limit", limit > 0 ? limit : 200);

        cmd.CommandText = $"""
            SELECT id, user_name, data_source_id, sql_text, started_at, duration_ms,
                   rows_affected, success, error_message, client_ip
            FROM audit_entries
            WHERE {where}
            ORDER BY started_at DESC
            LIMIT $limit;
            """;

        var result = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new AuditEntry
            {
                Id = Guid.ParseExact(reader.GetString(0), "N"),
                UserName = reader.GetString(1),
                DataSourceId = Guid.ParseExact(reader.GetString(2), "N"),
                SqlText = reader.GetString(3),
                StartedAt = DateTimeOffset.Parse(reader.GetString(4)),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                RowsAffected = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                Success = reader.GetInt64(7) != 0,
                ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                ClientIp = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        if (!_initialized)
            await InitializeAsync(connection, ct);
        return connection;
    }

    /// <summary>Ленивая инициализация схемы: таблица + индексы по времени/пользователю/датасорсу.</summary>
    private async Task InitializeAsync(SqliteConnection connection, CancellationToken ct)
    {
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS audit_entries (
                    id             TEXT PRIMARY KEY,
                    user_name      TEXT    NOT NULL,
                    data_source_id TEXT    NOT NULL,
                    sql_text       TEXT    NOT NULL,
                    started_at     TEXT    NOT NULL,
                    duration_ms    INTEGER NOT NULL,
                    rows_affected  INTEGER NULL,
                    success        INTEGER NOT NULL,
                    error_message  TEXT    NULL,
                    client_ip      TEXT    NULL
                );
                CREATE INDEX IF NOT EXISTS ix_audit_started_at ON audit_entries (started_at);
                CREATE INDEX IF NOT EXISTS ix_audit_user       ON audit_entries (user_name, started_at);
                CREATE INDEX IF NOT EXISTS ix_audit_ds         ON audit_entries (data_source_id, started_at);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
