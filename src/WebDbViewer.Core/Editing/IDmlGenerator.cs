namespace WebDbViewer.Core.Editing;

/// <summary>
/// Генератор параметризованного DML под диалект СУБД.
/// Инварианты реализаций:
///  - значения передаются ТОЛЬКО параметрами (никакой конкатенации в SQL);
///  - идентификаторы квотируются по правилам диалекта;
///  - Update/Delete без ключевых значений — ошибка (защита от массового изменения);
///  - WHERE строится по PK, а для таблиц без PK — по ctid (PostgreSQL) / ROWID (Oracle).
/// </summary>
public interface IDmlGenerator
{
    /// <summary>Диалект, для которого генерируется SQL.</summary>
    DbKind Kind { get; }

    /// <summary>INSERT одной строки из <see cref="RowEdit.ChangedValues"/>.</summary>
    DmlCommand BuildInsert(TableInfo table, RowEdit edit);

    /// <summary>UPDATE одной строки: SET из ChangedValues, WHERE из KeyValues.</summary>
    DmlCommand BuildUpdate(TableInfo table, RowEdit edit);

    /// <summary>DELETE одной строки: WHERE из KeyValues.</summary>
    DmlCommand BuildDelete(TableInfo table, RowEdit edit);
}

/// <summary>Вспомогательная диспетчеризация по виду операции.</summary>
public static class DmlGeneratorExtensions
{
    /// <summary>Строит DML-оператор согласно <see cref="RowEdit.Kind"/>.</summary>
    public static DmlCommand Build(this IDmlGenerator generator, TableInfo table, RowEdit edit) => edit.Kind switch
    {
        RowEditKind.Insert => generator.BuildInsert(table, edit),
        RowEditKind.Update => generator.BuildUpdate(table, edit),
        RowEditKind.Delete => generator.BuildDelete(table, edit),
        _ => throw new InvalidOperationException($"Неизвестный вид операции: {edit.Kind}."),
    };
}
