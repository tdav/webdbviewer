namespace WebDbViewer.Storage.Postgres;

/// <summary>
/// Настройки метабазы приложения (PostgreSQL): здесь хранятся ВСЕ настройки WebDbViewer —
/// датасорсы, пользователи, ключи Data Protection, снапшоты метаданных и журнал аудита.
/// </summary>
public sealed class PostgresStorageOptions
{
    public const string SectionName = "MetaStore";

    /// <summary>Строка подключения к метабазе (Npgsql).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Схема, в которой создаются служебные таблицы.</summary>
    public string Schema { get; set; } = "webdbviewer";

    /// <summary>
    /// Одноразовый импорт ключей Data Protection из локальной папки в метабазу
    /// (нужен при переезде с файлового хранилища, иначе ранее зашифрованные пароли не расшифруются).
    /// </summary>
    public string? ImportKeysFromDirectory { get; set; }
}
