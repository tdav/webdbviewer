using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core.Security;

namespace WebDbViewer.Web.Pages;

/// <summary>Страница входа: учётные записи хранятся в метабазе PostgreSQL.</summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly IUserStore _users;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IUserStore users, ILogger<LoginModel> logger)
    {
        _users = users;
        _logger = logger;
    }

    public string? ErrorMessage { get; private set; }
    public string? Username { get; private set; }
    public string? ReturnUrl { get; private set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect("/editor");

        ReturnUrl = returnUrl;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string username, string password, string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
        Username = username;

        var user = await _users.FindAsync(username ?? "", HttpContext.RequestAborted);
        var passOk = user is not null
            && Security.PasswordHasher.Verify(password ?? "", user.PasswordHash);

        if (user is null || !passOk)
        {
            _logger.LogWarning("Неудачная попытка входа: пользователь «{User}», IP {Ip}",
                username, HttpContext.Connection.RemoteIpAddress);
            ErrorMessage = "Неверное имя пользователя или пароль.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        _logger.LogInformation("Пользователь «{User}» вошёл в систему", user.Username);

        if (user.MustChangePassword)
            _logger.LogWarning("Учётная запись «{User}» использует пароль по умолчанию — смените его", user.Username);

        // Защита от open redirect: разрешаем только локальные адреса
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return Redirect("/editor");
    }

    /// <summary>Выход из системы (POST /login?handler=Logout).</summary>
    public async Task<IActionResult> OnPostLogoutAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/login");
    }
}
