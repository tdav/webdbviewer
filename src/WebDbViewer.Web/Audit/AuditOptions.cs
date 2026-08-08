namespace WebDbViewer.Web.Audit;

/// <summary>Настройки аудита запросов.</summary>
public sealed class AuditOptions
{
    /// <summary>Путь к отдельному файлу SQLite журнала аудита.</summary>
    public string DbPath { get; set; } = "audit.db";
}
