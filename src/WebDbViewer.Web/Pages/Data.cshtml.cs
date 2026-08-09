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
    /// <summary>База данных объекта: из query-параметра db либо база датасорса.</summary>
    public string Database { get; private set; } = "";
    /// <summary>true — объект в базе, отличной от базы подключения (навигатор по всем базам).</summary>
    public bool IsOtherDatabase { get; private set; }
    public bool IsProduction { get; private set; }
    public bool IsReadOnly { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid ds, string schema, string table, string? db, CancellationToken ct)
    {
        var config = await _store.GetAsync(ds, ct);
        if (config is null)
            return NotFound("Датасорс не найден");

        DsId = ds;
        Schema = schema;
        Table = table;
        Database = string.IsNullOrWhiteSpace(db) ? config.Database : db;
        IsOtherDatabase = !string.Equals(Database, config.Database, StringComparison.Ordinal);
        DataSourceName = config.Name;
        IsProduction = config.IsProduction;
        IsReadOnly = config.ReadOnly;
        return Page();
    }
}
