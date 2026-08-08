namespace WebDbViewer.Core.Editing;

/// <summary>Вид операции изменения строки.</summary>
public enum RowEditKind { Insert, Update, Delete }

/// <summary>
/// Одно изменение строки таблицы (inline-редактирование данных).
/// Идентификация строки — по значениям PK; для таблиц без PK — по псевдоколонке
/// адреса строки (ctid для PostgreSQL, ROWID/__ROWID для Oracle) в <see cref="KeyValues"/>.
/// </summary>
public sealed record RowEdit
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required RowEditKind Kind { get; init; }

    /// <summary>
    /// Ключевые значения строки (колонка → значение): PK либо ctid/ROWID.
    /// Обязательны для Update/Delete; для Insert игнорируются.
    /// </summary>
    public IReadOnlyDictionary<string, object?> KeyValues { get; init; }
        = new Dictionary<string, object?>();

    /// <summary>
    /// Изменённые значения (колонка → новое значение; null = NULL).
    /// Обязательны для Update; для Insert — вставляемые значения (пусто = все DEFAULT).
    /// </summary>
    public IReadOnlyDictionary<string, object?> ChangedValues { get; init; }
        = new Dictionary<string, object?>();
}
