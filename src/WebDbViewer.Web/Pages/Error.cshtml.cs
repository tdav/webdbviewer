using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebDbViewer.Web.Pages;

/// <summary>Страница ошибки (UseExceptionHandler).</summary>
[AllowAnonymous]
public sealed class ErrorModel : PageModel
{
    public void OnGet()
    {
    }
}
