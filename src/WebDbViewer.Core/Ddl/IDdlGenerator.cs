using System.Data.Common;

namespace WebDbViewer.Core.Ddl;

/// <summary>
/// Генератор DDL-текста объектов БД под конкретный диалект.
/// Реализации: PostgreSQL — сборка CREATE TABLE из системного каталога
/// (pg_get_constraintdef / pg_get_indexdef / pg_get_viewdef / pg_get_functiondef),
/// Oracle — DBMS_METADATA.GET_DDL (+ ALL_SOURCE как fallback для пакетов).
/// Все методы бросают <see cref="DdlObjectNotFoundException"/>, если объект не найден.
/// </summary>
public interface IDdlGenerator
{
    /// <summary>Диалект, для которого генерируется DDL.</summary>
    DbKind Kind { get; }

    /// <summary>DDL таблицы: CREATE TABLE + ограничения (PK/FK/UNIQUE/CHECK) + индексы + комментарии.</summary>
    Task<string> GetTableDdlAsync(DbConnection connection, string schema, string table, CancellationToken ct);

    /// <summary>DDL представления (обычного или материализованного).</summary>
    Task<string> GetViewDdlAsync(DbConnection connection, string schema, string view, CancellationToken ct);

    /// <summary>DDL индекса.</summary>
    Task<string> GetIndexDdlAsync(DbConnection connection, string schema, string index, CancellationToken ct);

    /// <summary>DDL функции/процедуры/пакета. routineType уточняет вид объекта (Function/Procedure/Package).</summary>
    Task<string> GetRoutineDdlAsync(DbConnection connection, string schema, string routine, DbObjectType routineType, CancellationToken ct);
}

/// <summary>Объект не найден в каталоге БД (для HTTP-слоя транслируется в 404).</summary>
public sealed class DdlObjectNotFoundException(string message) : InvalidOperationException(message);
