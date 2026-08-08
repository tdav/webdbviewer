namespace WebDbViewer.Web.Security;

/// <summary>Настройки простой формы логина: один пользователь из конфигурации.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>Имя пользователя (по умолчанию admin).</summary>
    public string Username { get; set; } = "admin";

    /// <summary>Хэш пароля в формате PBKDF2-SHA256$итерации$salt$hash (см. <see cref="PasswordHasher"/>).</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Время жизни cookie-сессии, минут.</summary>
    public int CookieLifetimeMinutes { get; set; } = 480;
}
