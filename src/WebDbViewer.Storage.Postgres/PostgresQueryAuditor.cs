using Npgsql;
using WebDbViewer.Core;

namespace WebDbViewer.Storage.Postgres;

/// <summary>
/// Журнал аудита выполненных запросов в метабазе PostgreSQL (замена audit.db на SQLite).
/// Обязателен для гос-сектора: кто, когда, на каком датасорсе, какой SQL, результат.
/// </summary>
public sealed class PostgresQueryAuditor : IQueryAuditor
{
    private readonly PostgresMetaStore meta;

    public PostgresQueryAuditor(PostgresMetaStore meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        this.meta = meta;
    }

    public async Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {meta.Schema}.audit_entries
                (id, user_name, data_source_id, sql_text, started_at, duration_ms,
                 rows_affected, success, error_message, client_ip)
            VALUES
                (@id, @user, @ds, @sql, @started, @duration,
                 @rows, @success, @error, @ip)
            ON CONFLICT (id) DO NOTHING
            """, connection);

        cmd.Parameters.AddWithValue("id", entry.Id);
        cmd.Parameters.AddWithValue("user", entry.UserName);
        cmd.Parameters.AddWithValue("ds", entry.DataSourceId);
        cmd.Parameters.AddWithValue("sql", entry.SqlText);
        cmd.Parameters.AddWithValue("started", entry.StartedAt);
        cmd.Parameters.AddWithValue("duration", (long)entry.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("rows", (object?)entry.RowsAffected ?? DBNull.Value);
        cmd.Parameters.AddWithValue("success", entry.Success);
        cmd.Parameters.AddWithValue("error", (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ip", (object?)entry.ClientIp ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEntry>> QueryAsync(
        DateTimeOffset from, DateTimeOffset to, string? userName, Guid? dataSourceId,
        int limit, CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();

        var where = "started_at >= @from AND started_at <= @to";
        cmd.Parameters.AddWithValue("from", from);
        cmd.Parameters.AddWithValue("to", to);
        if (!string.IsNullOrWhiteSpace(userName))
        {
            where += " AND user_name = @user";
            cmd.Parameters.AddWithValue("user", userName);
        }
        if (dataSourceId is { } ds)
        {
            where += " AND data_source_id = @ds";
            cmd.Parameters.AddWithValue("ds", ds);
        }
        cmd.Parameters.AddWithValue("limit", limit > 0 ? limit : 200);

        cmd.CommandText = $"""
            SELECT id, user_name, data_source_id, sql_text, started_at, duration_ms,
                   rows_affected, success, error_message, client_ip
            FROM {meta.Schema}.audit_entries
            WHERE {where}
            ORDER BY started_at DESC
            LIMIT @limit
            """;

        var result = new List<AuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new AuditEntry
            {
                Id = reader.GetGuid(0),
                UserName = reader.GetString(1),
                DataSourceId = reader.GetGuid(2),
                SqlText = reader.GetString(3),
                StartedAt = reader.GetFieldValue<DateTimeOffset>(4),
                Duration = TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                RowsAffected = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                Success = reader.GetBoolean(7),
                ErrorMessage = reader.IsDBNull(8) ? null : reader.GetString(8),
                ClientIp = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }
        return result;
    }
}
