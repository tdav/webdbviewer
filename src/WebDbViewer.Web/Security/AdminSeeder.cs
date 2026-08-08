using WebDbViewer.Core.Security;

namespace WebDbViewer.Web.Security;

/// <summary>
/// Первичная инициализация учётных записей в метабазе: если пользователей ещё нет,
/// создаётся первый администратор.
/// </summary>
/// <remarks>
/// Пароль первого администратора определяется в следующем порядке:
/// <list type="number">
///   <item><c>Auth:InitialPassword</c> — пароль открытым текстом (хэшируется при сохранении);</item>
///   <item><c>Auth:PasswordHash</c> — готовый PBKDF2-хэш (перенос ранее настроенной учётки);</item>
///   <item>значение по умолчанию <c>admin</c> — только для первого запуска, требует смены.</item>
/// </list>
/// </remarks>
public static class AdminSeeder
{
    /// <summary>Пароль администратора по умолчанию при первом запуске.</summary>
    public const string DefaultPassword = "admin";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var users = services.GetRequiredService<IUserStore>();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AdminSeeder));

        if (await users.CountAsync(ct).ConfigureAwait(false) > 0)
            return;

        var userName = string.IsNullOrWhiteSpace(options.Username) ? "admin" : options.Username.Trim();
        var (hash, source, mustChange) = ResolveInitialPassword(options);

        await users.SaveAsync(
            new AppUser
            {
                Username = userName,
                PasswordHash = hash,
                Role = "admin",
                MustChangePassword = mustChange,
            },
            ct).ConfigureAwait(false);

        if (mustChange)
        {
            logger.LogWarning(
                "Создана первая учётная запись «{User}» с паролем по умолчанию «{Password}». " +
                "СМЕНИТЕ ПАРОЛЬ: задайте Auth:InitialPassword до первого запуска либо обновите " +
                "password_hash в таблице users метабазы.",
                userName, DefaultPassword);
        }
        else
        {
            logger.LogInformation(
                "Создана первая учётная запись «{User}» (источник пароля: {Source})", userName, source);
        }
    }

    private static (string Hash, string Source, bool MustChange) ResolveInitialPassword(AuthOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.InitialPassword))
            return (PasswordHasher.Hash(options.InitialPassword), "Auth:InitialPassword", false);

        if (!string.IsNullOrWhiteSpace(options.PasswordHash))
            return (options.PasswordHash, "Auth:PasswordHash", false);

        return (PasswordHasher.Hash(DefaultPassword), "по умолчанию", true);
    }
}
