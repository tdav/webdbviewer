using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using WebDbViewer.Core;

namespace WebDbViewer.Metadata;

/// <summary>
/// Persistent-хранилище снапшотов схем на SQLite (Microsoft.Data.Sqlite).
/// Таблица snapshots(datasource_id, schema, json, version_hash, loaded_at).
/// Сериализация SchemaSnapshot — System.Text.Json.
/// </summary>
public sealed class SqliteSnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteSnapshotStore(IOptions<MetadataCacheOptions> options)
        : this(options.Value.SqlitePath)
    {
    }

    public SqliteSnapshotStore(string sqlitePath)
    {
        // Создаём директорию под файл БД, если её ещё нет
        var dir = Path.GetDirectoryName(Path.GetFullPath(sqlitePath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    /// <summary>Открывает соединение, при первом обращении создаёт схему таблицы.</summary>
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        if (!_initialized)
        {
            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_initialized)
                {
                    var cmd = connection.CreateCommand();
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS snapshots (
                            datasource_id TEXT NOT NULL,
                            schema        TEXT NOT NULL COLLATE NOCASE,
                            json          TEXT NOT NULL,
                            version_hash  TEXT NULL,
                            loaded_at     TEXT NOT NULL,
                            PRIMARY KEY (datasource_id, schema)
                        );
                        """;
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        return connection;
    }

    public async Task SaveAsync(Guid dataSourceId, SchemaSnapshot snapshot, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots (datasource_id, schema, json, version_hash, loaded_at)
            VALUES (@ds, @schema, @json, @hash, @loadedAt)
            ON CONFLICT (datasource_id, schema) DO UPDATE SET
                json = excluded.json,
                version_hash = excluded.version_hash,
                loaded_at = excluded.loaded_at;
            """;
        cmd.Parameters.AddWithValue("@ds", dataSourceId.ToString("D"));
        cmd.Parameters.AddWithValue("@schema", snapshot.SchemaName);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(snapshot, JsonOptions));
        cmd.Parameters.AddWithValue("@hash", (object?)snapshot.VersionHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@loadedAt", snapshot.LoadedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PersistedSnapshot>> LoadAllAsync(CancellationToken ct)
    {
        var result = new List<PersistedSnapshot>();
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT datasource_id, json FROM snapshots";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!Guid.TryParse(reader.GetString(0), out var dsId))
                continue; // повреждённая запись — пропускаем
            var snapshot = TryDeserialize(reader.GetString(1));
            if (snapshot is not null)
                result.Add(new PersistedSnapshot(dsId, snapshot));
        }
        return result;
    }

    public async Task<SchemaSnapshot?> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT json FROM snapshots WHERE datasource_id = @ds AND schema = @schema";
        cmd.Parameters.AddWithValue("@ds", dataSourceId.ToString("D"));
        cmd.Parameters.AddWithValue("@schema", schema);
        var json = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return json is null ? null : TryDeserialize(json);
    }

    public async Task DeleteAsync(Guid dataSourceId, string? schema, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var cmd = connection.CreateCommand();
        if (schema is null)
        {
            cmd.CommandText = "DELETE FROM snapshots WHERE datasource_id = @ds";
        }
        else
        {
            cmd.CommandText = "DELETE FROM snapshots WHERE datasource_id = @ds AND schema = @schema";
            cmd.Parameters.AddWithValue("@schema", schema);
        }
        cmd.Parameters.AddWithValue("@ds", dataSourceId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Десериализация с защитой от повреждённого JSON (возврат null вместо исключения).</summary>
    private static SchemaSnapshot? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SchemaSnapshot>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
