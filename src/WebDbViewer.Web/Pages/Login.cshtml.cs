using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using WebDbViewer.Web.Security;

namespace WebDbViewer.Web.Pages;

/// <summary>Страница входа: простая форма логина с одним пользователем из конфигурации.</summary>
[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    private readonly IOptions<AuthOptions> _auth;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(IOptions<AuthOptions> auth, ILogger<LoginModel> logger)
    {
        _auth = auth;
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

        var options = _auth.Value;
        var userOk = string.Equals(username?.Trim(), options.Username, StringComparison.OrdinalIgnoreCase);
        var passOk = PasswordHasher.Verify(password ?? "", options.PasswordHash);

        if (!userOk || !passOk)
        {
            _logger.LogWarning("Неудачная попытка входа: пользователь «{User}», IP {Ip}",
                username, HttpContext.Connection.RemoteIpAddress);
            ErrorMessage = "Неверное имя пользователя или пароль.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, options.Username),
            new(ClaimTypes.Role, "admin")
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        _logger.LogInformation("Пользователь «{User}» вошёл в систему", options.Username);

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
