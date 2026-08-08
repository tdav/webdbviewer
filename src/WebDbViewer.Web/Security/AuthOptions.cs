namespace WebDbViewer.Web.Security;

/// <summary>
/// Настройки аутентификации. Учётные записи хранятся в метабазе PostgreSQL;
/// значения ниже используются ТОЛЬКО при первичной инициализации (см. <see cref="AdminSeeder"/>).
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Имя первого администратора (по умолчанию admin).</summary>
    public string Username { get; set; } = "admin";

    /// <summary>
    /// Пароль первого администратора открытым текстом. Задаётся только для первичной инициализации
    /// (лучше через переменную окружения или user-secrets), после сида не используется.
    /// </summary>
    public string InitialPassword { get; set; } = "";

    /// <summary>
    /// Готовый хэш пароля в формате PBKDF2-SHA256$итерации$salt$hash (см. <see cref="PasswordHasher"/>).
    /// Используется при сиде, если <see cref="InitialPassword"/> не задан — позволяет перенести
    /// ранее настроенную учётную запись из appsettings в метабазу.
    /// </summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Время жизни cookie-сессии, минут.</summary>
    public int CookieLifetimeMinutes { get; set; } = 480;
}
