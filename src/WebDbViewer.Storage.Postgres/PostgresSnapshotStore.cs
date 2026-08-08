using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using WebDbViewer.Core;
using WebDbViewer.Metadata;

namespace WebDbViewer.Storage.Postgres;

/// <summary>
/// Persistent-хранилище снапшотов схем в метабазе PostgreSQL (замена SQLite-снапшота).
/// Ключ — пара (датасорс, имя схемы без учёта регистра); JSON снапшота хранится в jsonb.
/// </summary>
public sealed class PostgresSnapshotStore : ISnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PostgresMetaStore meta;

    public PostgresSnapshotStore(PostgresMetaStore meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        this.meta = meta;
    }

    public async Task SaveAsync(Guid dataSourceId, SchemaSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {meta.Schema}.schema_snapshots
                (datasource_id, schema_key, schema_name, json, version_hash, loaded_at)
            VALUES (@ds, @key, @name, @json, @hash, @loadedAt)
            ON CONFLICT (datasource_id, schema_key) DO UPDATE SET
                schema_name  = excluded.schema_name,
                json         = excluded.json,
                version_hash = excluded.version_hash,
                loaded_at    = excluded.loaded_at
            """, connection);

        cmd.Parameters.AddWithValue("ds", dataSourceId);
        cmd.Parameters.AddWithValue("key", Key(snapshot.SchemaName));
        cmd.Parameters.AddWithValue("name", snapshot.SchemaName);
        cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(snapshot, JsonOptions)
        });
        cmd.Parameters.AddWithValue("hash", (object?)snapshot.VersionHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("loadedAt", snapshot.LoadedAt);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PersistedSnapshot>> LoadAllAsync(CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT datasource_id, json FROM {meta.Schema}.schema_snapshots", connection);

        var result = new List<PersistedSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var snapshot = TryDeserialize(reader.GetString(1));
            if (snapshot is not null)
                result.Add(new PersistedSnapshot(reader.GetGuid(0), snapshot));
        }
        return result;
    }

    public async Task<SchemaSnapshot?> LoadAsync(Guid dataSourceId, string schema, CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT json FROM {meta.Schema}.schema_snapshots WHERE datasource_id = @ds AND schema_key = @key",
            connection);
        cmd.Parameters.AddWithValue("ds", dataSourceId);
        cmd.Parameters.AddWithValue("key", Key(schema));

        var json = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return json is null ? null : TryDeserialize(json);
    }

    public async Task DeleteAsync(Guid dataSourceId, string? schema, CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            schema is null
                ? $"DELETE FROM {meta.Schema}.schema_snapshots WHERE datasource_id = @ds"
                : $"DELETE FROM {meta.Schema}.schema_snapshots WHERE datasource_id = @ds AND schema_key = @key",
            connection);
        cmd.Parameters.AddWithValue("ds", dataSourceId);
        if (schema is not null)
            cmd.Parameters.AddWithValue("key", Key(schema));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Регистронезависимый ключ схемы (PG отдаёт lowercase, Oracle — UPPERCASE).</summary>
    private static string Key(string schema) => schema.ToLowerInvariant();

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
