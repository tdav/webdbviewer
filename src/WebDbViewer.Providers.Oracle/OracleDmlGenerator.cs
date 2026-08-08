using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;
using WebDbViewer.Core.Editing;

namespace WebDbViewer.Providers.Oracle;

/// <summary>
/// Генератор параметризованного DML для Oracle.
/// WHERE — по PK; для таблиц без PK — по псевдоколонке ROWID
/// (клиент присылает её под именем ROWID или __ROWID — алиас из SELECT-страницы).
/// Значения — только через параметры :s{i}/:k{i}.
/// </summary>
public sealed class OracleDmlGenerator : IDmlGenerator
{
    /// <summary>Провайдер используется только для квотирования идентификаторов (без состояния).</summary>
    private static readonly OracleProvider Quoting = new();

    public DbKind Kind => DbKind.Oracle;

    public DmlCommand BuildInsert(TableInfo table, RowEdit edit)
    {
        EnsureKind(edit, RowEditKind.Insert);
        if (edit.ChangedValues.Count == 0)
            throw new InvalidOperationException("Не заданы значения для вставки строки (Oracle не поддерживает INSERT без значений).");

        var columns = new List<string>();
        var values = new List<string>();
        var parameters = new List<DmlParameter>();
        foreach (var (name, value) in edit.ChangedValues)
        {
            var column = FindColumn(table, name);
            EnsureNotGenerated(table, column);
            var p = $"s{parameters.Count}";
            columns.Add(Quote(column.Name));
            values.Add($":{p}");
            parameters.Add(new DmlParameter(p, value));
        }

        return new DmlCommand
        {
            Sql = $"INSERT INTO {Target(table)} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})",
            Parameters = parameters,
        };
    }

    public DmlCommand BuildUpdate(TableInfo table, RowEdit edit)
    {
        EnsureKind(edit, RowEditKind.Update);
        if (edit.ChangedValues.Count == 0)
            throw new InvalidOperationException("Нет изменённых значений для обновления строки.");

        var parameters = new List<DmlParameter>();
        var set = new List<string>();
        foreach (var (name, value) in edit.ChangedValues)
        {
            var column = FindColumn(table, name);
            EnsureNotGenerated(table, column);
            var p = $"s{parameters.Count}";
            set.Add($"{Quote(column.Name)} = :{p}");
            parameters.Add(new DmlParameter(p, value));
        }

        var where = BuildWhere(table, edit, parameters);
        return new DmlCommand
        {
            Sql = new StringBuilder("UPDATE ").Append(Target(table))
                .Append(" SET ").Append(string.Join(", ", set))
                .Append(" WHERE ").Append(where).ToString(),
            Parameters = parameters,
        };
    }

    public DmlCommand BuildDelete(TableInfo table, RowEdit edit)
    {
        EnsureKind(edit, RowEditKind.Delete);
        var parameters = new List<DmlParameter>();
        var where = BuildWhere(table, edit, parameters);
        return new DmlCommand
        {
            Sql = $"DELETE FROM {Target(table)} WHERE {where}",
            Parameters = parameters,
        };
    }

    // ---------------------------------------------------------------- Вспомогательное

    /// <summary>WHERE по ключевым значениям: PK-колонки либо псевдоколонка ROWID; NULL — через IS NULL.</summary>
    private static string BuildWhere(TableInfo table, RowEdit edit, List<DmlParameter> parameters)
    {
        if (edit.KeyValues.Count == 0)
            throw new InvalidOperationException(
                $"Не заданы ключевые значения строки таблицы «{table.Schema}.{table.Name}» — операция запрещена.");

        var predicates = new List<string>();
        foreach (var (name, value) in edit.KeyValues)
        {
            if (IsRowIdKey(name))
            {
                // Адрес строки для таблиц без PK; неявное преобразование VARCHAR2 → ROWID.
                var p = $"k{parameters.Count}";
                predicates.Add($"ROWID = :{p}");
                parameters.Add(new DmlParameter(p, value?.ToString()));
                continue;
            }

            var column = FindColumn(table, name);
            if (value is null)
            {
                predicates.Add($"{Quote(column.Name)} IS NULL");
            }
            else
            {
                var p = $"k{parameters.Count}";
                predicates.Add($"{Quote(column.Name)} = :{p}");
                parameters.Add(new DmlParameter(p, value));
            }
        }
        return string.Join(" AND ", predicates);
    }

    /// <summary>ROWID либо __ROWID (алиас псевдоколонки из SELECT-страницы данных).</summary>
    private static bool IsRowIdKey(string name) =>
        string.Equals(name, "ROWID", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "__ROWID", StringComparison.OrdinalIgnoreCase);

    private static string Target(TableInfo table) => $"{Quote(table.Schema)}.{Quote(table.Name)}";

    private static string Quote(string identifier) => Quoting.QuoteIdentifier(identifier);

    private static ColumnInfo FindColumn(TableInfo table, string name) =>
        table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
        ?? table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Колонка «{name}» не найдена в таблице «{table.Schema}.{table.Name}».");

    private static void EnsureNotGenerated(TableInfo table, ColumnInfo column)
    {
        if (column.IsGenerated)
            throw new InvalidOperationException(
                $"Колонка «{column.Name}» таблицы «{table.Schema}.{table.Name}» является генерируемой и не может быть изменена.");
    }

    private static void EnsureKind(RowEdit edit, RowEditKind expected)
    {
        if (edit.Kind != expected)
            throw new InvalidOperationException($"Ожидалась операция {expected}, получена {edit.Kind}.");
    }
}

/// <summary>DI-регистрация генератора DML Oracle.</summary>
public static class OracleDmlGeneratorExtensions
{
    /// <summary>Регистрирует <see cref="OracleDmlGenerator"/> как <see cref="IDmlGenerator"/> (singleton).</summary>
    public static IServiceCollection AddOracleDmlGeneration(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDmlGenerator, OracleDmlGenerator>());
        return services;
    }
}
