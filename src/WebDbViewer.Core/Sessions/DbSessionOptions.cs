namespace WebDbViewer.Core.Sessions;

/// <summary>Настройки менеджера stateful-сессий БД и файлового хранилища датасорсов.</summary>
public sealed class DbSessionOptions
{
    /// <summary>Максимальное число одновременных сессий на одного пользователя.</summary>
    public int MaxSessionsPerUser { get; set; } = 5;

    /// <summary>TTL простоя: сессия, не использовавшаяся дольше этого времени, закрывается фоновым свипером.</summary>
    public TimeSpan IdleTtl { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Периодичность фоновой очистки просроченных сессий.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Путь к JSON-файлу с конфигурациями датасорсов (пароли — только в защищённом виде).</summary>
    public string DataSourcesFilePath { get; set; } = Path.Combine("App_Data", "datasources.json");
}
