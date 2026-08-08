namespace WebDbViewer.Core.Security;

/// <summary>Учётная запись пользователя приложения (хранится в метабазе).</summary>
public sealed record AppUser
{
    /// <summary>Имя пользователя в нормализованном виде (нижний регистр).</summary>
    public required string Username { get; init; }

    /// <summary>Хэш пароля в формате PBKDF2-SHA256$итерации$salt$hash.</summary>
    public required string PasswordHash { get; init; }

    /// <summary>Роль (пока используется единственная — admin).</summary>
    public string Role { get; init; } = "admin";

    /// <summary>Признак пароля, выданного при первичной инициализации: требуется смена.</summary>
    public bool MustChangePassword { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Хранилище учётных записей приложения (метабаза).</summary>
public interface IUserStore
{
    /// <summary>Поиск пользователя по имени без учёта регистра; null — если не найден.</summary>
    Task<AppUser?> FindAsync(string username, CancellationToken ct);

    /// <summary>Все учётные записи (для администрирования).</summary>
    Task<IReadOnlyList<AppUser>> GetAllAsync(CancellationToken ct);

    /// <summary>Количество учётных записей (для определения «первого запуска»).</summary>
    Task<int> CountAsync(CancellationToken ct);

    /// <summary>Создаёт или обновляет учётную запись (upsert по имени).</summary>
    Task SaveAsync(AppUser user, CancellationToken ct);

    /// <summary>Удаляет учётную запись.</summary>
    Task DeleteAsync(string username, CancellationToken ct);
}
