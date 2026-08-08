using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebDbViewer.Web.Pages;

/// <summary>Главная страница — перенаправляет в SQL-редактор.</summary>
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => Redirect("/editor");
}
