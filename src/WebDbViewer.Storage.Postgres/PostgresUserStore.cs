using Npgsql;
using WebDbViewer.Core.Security;

namespace WebDbViewer.Storage.Postgres;

/// <summary>
/// Учётные записи приложения в метабазе PostgreSQL.
/// Хранится только хэш пароля (PBKDF2-SHA256), имя пользователя нормализуется в нижний регистр.
/// </summary>
public sealed class PostgresUserStore : IUserStore
{
    private const string Columns = "username, password_hash, role, must_change_password, created_at, updated_at";

    private readonly PostgresMetaStore meta;

    public PostgresUserStore(PostgresMetaStore meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        this.meta = meta;
    }

    public async Task<AppUser?> FindAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {meta.Schema}.users WHERE username = @name", connection);
        cmd.Parameters.AddWithValue("name", Normalize(username));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<AppUser>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM {meta.Schema}.users ORDER BY username", connection);

        var result = new List<AppUser>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(Map(reader));
        return result;
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {meta.Schema}.users", connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
    }

    public async Task SaveAsync(AppUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {meta.Schema}.users (username, password_hash, role, must_change_password)
            VALUES (@name, @hash, @role, @mustChange)
            ON CONFLICT (username) DO UPDATE SET
                password_hash        = excluded.password_hash,
                role                 = excluded.role,
                must_change_password = excluded.must_change_password,
                updated_at           = now()
            """, connection);
        cmd.Parameters.AddWithValue("name", Normalize(user.Username));
        cmd.Parameters.AddWithValue("hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("role", user.Role);
        cmd.Parameters.AddWithValue("mustChange", user.MustChangePassword);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string username, CancellationToken ct)
    {
        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM {meta.Schema}.users WHERE username = @name", connection);
        cmd.Parameters.AddWithValue("name", Normalize(username));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Атомарно создаёт учётную запись, если её ещё нет.
    /// Возвращает true, если запись была создана именно этим вызовом (нужно для сида первого админа
    /// без гонок при параллельном старте нескольких экземпляров).
    /// </summary>
    public async Task<bool> TryCreateAsync(AppUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var connection = await meta.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand($"""
            INSERT INTO {meta.Schema}.users (username, password_hash, role, must_change_password)
            VALUES (@name, @hash, @role, @mustChange)
            ON CONFLICT (username) DO NOTHING
            """, connection);
        cmd.Parameters.AddWithValue("name", Normalize(user.Username));
        cmd.Parameters.AddWithValue("hash", user.PasswordHash);
        cmd.Parameters.AddWithValue("role", user.Role);
        cmd.Parameters.AddWithValue("mustChange", user.MustChangePassword);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();

    private static AppUser Map(NpgsqlDataReader reader) => new()
    {
        Username = reader.GetString(0),
        PasswordHash = reader.GetString(1),
        Role = reader.GetString(2),
        MustChangePassword = reader.GetBoolean(3),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(5),
    };
}
