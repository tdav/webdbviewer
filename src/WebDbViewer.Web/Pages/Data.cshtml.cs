using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Pages;

/// <summary>Просмотр данных таблицы: контейнер грида + панель фильтра WHERE и сортировки.</summary>
public sealed class DataModel : PageModel
{
    private readonly IDataSourceStore _store;

    public DataModel(IDataSourceStore store) => _store = store;

    public Guid DsId { get; private set; }
    public string Schema { get; private set; } = "";
    public string Table { get; private set; } = "";
    public string DataSourceName { get; private set; } = "";
    public bool IsProduction { get; private set; }
    public bool IsReadOnly { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid ds, string schema, string table, CancellationToken ct)
    {
        var config = await _store.GetAsync(ds, ct);
        if (config is null)
            return NotFound("Датасорс не найден");

        DsId = ds;
        Schema = schema;
        Table = table;
        DataSourceName = config.Name;
        IsProduction = config.IsProduction;
        IsReadOnly = config.ReadOnly;
        return Page();
    }
}
