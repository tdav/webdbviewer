using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using WebDbViewer.Core;

namespace WebDbViewer.Storage.Postgres;

/// <summary>
/// Хранилище конфигураций датасорсов в метабазе PostgreSQL (замена файлового JSON-хранилища).
/// Пароли хранятся ТОЛЬКО в защищённом виде (<see cref="DataSourceConfig.ProtectedPassword"/>).
/// </summary>
public sealed class PostgresDataSourceStore : IDataSourceStore
{
    private const string Columns = """
        id, name, kind, host, port, database, username, protected_password,
        read_only, is_production, use_ssl, connect_timeout_seconds,
        command_timeout_seconds, max_pool_size_per_user, extra
        """;

    private readonly PostgresMetaStore meta;

    public PostgresDataSourceStore(PostgresMetaStore meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        this.meta = meta;
    }

    public async Task<IReadOnlyList<DataSourceConfig>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {meta.Schema}.datasources ORDER BY lower(name)", connection);

        var result = new List<DataSourceConfig>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(Map(reader));
        return result;
    }

    public async Task<DataSourceConfig?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {meta.Schema}.datasources WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task SaveAsync(DataSourceConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {meta.Schema}.datasources
                (id, name, kind, host, port, database, username, protected_password,
                 read_only, is_production, use_ssl, connect_timeout_seconds,
                 command_timeout_seconds, max_pool_size_per_user, extra, updated_at)
            VALUES
                (@id, @name, @kind, @host, @port, @database, @username, @password,
                 @readOnly, @isProduction, @useSsl, @connectTimeout,
                 @commandTimeout, @maxPool, @extra, now())
            ON CONFLICT (id) DO UPDATE SET
                name                    = excluded.name,
                kind                    = excluded.kind,
                host                    = excluded.host,
                port                    = excluded.port,
                database                = excluded.database,
                username                = excluded.username,
                protected_password      = excluded.protected_password,
                read_only               = excluded.read_only,
                is_production           = excluded.is_production,
                use_ssl                 = excluded.use_ssl,
                connect_timeout_seconds = excluded.connect_timeout_seconds,
                command_timeout_seconds = excluded.command_timeout_seconds,
                max_pool_size_per_user  = excluded.max_pool_size_per_user,
                extra                   = excluded.extra,
                updated_at              = now()
            """, connection);

        cmd.Parameters.AddWithValue("id", config.Id);
        cmd.Parameters.AddWithValue("name", config.Name);
        cmd.Parameters.AddWithValue("kind", config.Kind.ToString());
        cmd.Parameters.AddWithValue("host", config.Host);
        cmd.Parameters.AddWithValue("port", config.Port);
        cmd.Parameters.AddWithValue("database", config.Database);
        cmd.Parameters.AddWithValue("username", config.Username);
        cmd.Parameters.AddWithValue("password", (object?)config.ProtectedPassword ?? DBNull.Value);
        cmd.Parameters.AddWithValue("readOnly", config.ReadOnly);
        cmd.Parameters.AddWithValue("isProduction", config.IsProduction);
        cmd.Parameters.AddWithValue("useSsl", config.UseSsl);
        cmd.Parameters.AddWithValue("connectTimeout", config.ConnectTimeoutSeconds);
        cmd.Parameters.AddWithValue("commandTimeout", config.CommandTimeoutSeconds);
        cmd.Parameters.AddWithValue("maxPool", config.MaxPoolSizePerUser);
        cmd.Parameters.Add(new NpgsqlParameter("extra", NpgsqlDbType.Jsonb)
        {
            Value = config.Extra is null or { Count: 0 }
                ? DBNull.Value
                : JsonSerializer.Serialize(config.Extra)
        });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {meta.Schema}.datasources WHERE id = @id", connection);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DataSourceConfig Map(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Name = reader.GetString(1),
        Kind = Enum.TryParse<DbKind>(reader.GetString(2), ignoreCase: true, out var kind) ? kind : DbKind.Postgres,
        Host = reader.GetString(3),
        Port = reader.GetInt32(4),
        Database = reader.GetString(5),
        Username = reader.GetString(6),
        ProtectedPassword = reader.IsDBNull(7) ? null : reader.GetString(7),
        ReadOnly = reader.GetBoolean(8),
        IsProduction = reader.GetBoolean(9),
        UseSsl = reader.GetBoolean(10),
        ConnectTimeoutSeconds = reader.GetInt32(11),
        CommandTimeoutSeconds = reader.GetInt32(12),
        MaxPoolSizePerUser = reader.GetInt32(13),
        Extra = reader.IsDBNull(14)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(14)),
    };
}
