using System.Data.Common;
using WebDbViewer.Core;
using WebDbViewer.Core.Ddl;

namespace WebDbViewer.Web.Api;

/// <summary>
/// Разбор параметра <c>type</c> и вызов нужного метода <see cref="IDdlGenerator"/>.
/// Используется и endpoint'ом /api/ddl, и обработчиком вкладки редактора,
/// поэтому список поддерживаемых типов существует в единственном экземпляре.
/// </summary>
public static class DdlText
{
    /// <summary>Типы, DDL которых собирается универсальным путём (GetObjectDdlAsync).</summary>
    private static readonly Dictionary<string, DbObjectType> ObjectTypes = new(StringComparer.Ordinal)
    {
        ["sequence"] = DbObjectType.Sequence,
        ["type"] = DbObjectType.Type,
        ["domain"] = DbObjectType.Domain,
        ["foreigntable"] = DbObjectType.ForeignTable,
        ["aggregate"] = DbObjectType.Aggregate,
        ["operator"] = DbObjectType.Operator,
        ["collation"] = DbObjectType.Collation,
        ["tsconfig"] = DbObjectType.TextSearchConfig,
        ["tsdictionary"] = DbObjectType.TextSearchDictionary,
        ["trigger"] = DbObjectType.Trigger,
        ["rule"] = DbObjectType.Rule,
        ["policy"] = DbObjectType.Policy,
    };

    /// <summary>Все типы, которые умеет удалять генератор (объектные + отношения и подпрограммы).</summary>
    private static readonly Dictionary<string, DbObjectType> DroppableTypes =
        new(ObjectTypes, StringComparer.Ordinal)
        {
            ["table"] = DbObjectType.Table,
            ["view"] = DbObjectType.View,
            ["matview"] = DbObjectType.MaterializedView,
            ["materializedview"] = DbObjectType.MaterializedView,
            ["index"] = DbObjectType.Index,
            ["function"] = DbObjectType.Function,
            ["procedure"] = DbObjectType.Procedure,
        };

    /// <summary>
    /// Скрипт DROP объекта; null — тип не распознан.
    /// qualifier уточняет объект среди одноимённых: таблица-владелец либо сигнатура перегрузки.
    /// </summary>
    public static async Task<string?> GetDropAsync(
        IDdlGenerator generator, DbConnection connection,
        string schema, string name, string type, string? qualifier, CancellationToken ct)
        => DroppableTypes.TryGetValue(type.ToLowerInvariant(), out var objectType)
            ? await generator.GetDropScriptAsync(connection, schema, name, objectType, qualifier, ct)
            : null;

    /// <summary>
    /// Текст DDL объекта; null — тип не распознан.
    /// owner — таблица-владелец для триггера, правила и политики RLS.
    /// </summary>
    public static async Task<string?> GetAsync(
        IDdlGenerator generator, DbConnection connection,
        string schema, string name, string type, string? owner, CancellationToken ct)
    {
        var normalized = type.ToLowerInvariant();
        return normalized switch
        {
            "table" => await generator.GetTableDdlAsync(connection, schema, name, ct),
            "view" or "matview" or "materializedview"
                => await generator.GetViewDdlAsync(connection, schema, name, ct),
            "index" => await generator.GetIndexDdlAsync(connection, schema, name, ct),
            "function" => await generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Function, ct),
            "procedure" => await generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Procedure, ct),
            "package" => await generator.GetRoutineDdlAsync(connection, schema, name, DbObjectType.Package, ct),
            _ when ObjectTypes.TryGetValue(normalized, out var objectType)
                => await generator.GetObjectDdlAsync(connection, schema, name, objectType, owner, ct),
            _ => null,
        };
    }
}
