using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WebDbViewer.Core;
using WebDbViewer.Core.Editing;

namespace WebDbViewer.Providers.Postgres;

/// <summary>
/// Генератор параметризованного DML для PostgreSQL.
/// WHERE — по PK; для таблиц без PK — по псевдоколонке ctid.
/// Значения — только через параметры @s{i}/@k{i}; с CAST к типу колонки,
/// чтобы строковые значения из UI корректно приводились к типу столбца.
/// </summary>
public sealed class PgDmlGenerator : IDmlGenerator
{
    /// <summary>Провайдер используется только для квотирования идентификаторов (без состояния).</summary>
    private static readonly PostgresProvider Quoting = new();

    public DbKind Kind => DbKind.Postgres;

    public DmlCommand BuildInsert(TableInfo table, RowEdit edit)
    {
        EnsureKind(edit, RowEditKind.Insert);
        var target = Target(table);

        // Пустой набор значений — вставка строки целиком из DEFAULT.
        if (edit.ChangedValues.Count == 0)
            return new DmlCommand { Sql = $"INSERT INTO {target} DEFAULT VALUES", Parameters = [] };

        var columns = new List<string>();
        var values = new List<string>();
        var parameters = new List<DmlParameter>();
        foreach (var (name, value) in edit.ChangedValues)
        {
            var column = FindColumn(table, name);
            EnsureNotGenerated(table, column);
            var p = $"s{parameters.Count}";
            columns.Add(Quote(column.Name));
            values.Add(Placeholder(p, column.DataType));
            parameters.Add(new DmlParameter(p, value));
        }

        return new DmlCommand
        {
            Sql = $"INSERT INTO {target} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", values)})",
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
            set.Add($"{Quote(column.Name)} = {Placeholder(p, column.DataType)}");
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

    /// <summary>WHERE по ключевым значениям: PK-колонки либо псевдоколонка ctid; NULL — через IS NULL.</summary>
    private static string BuildWhere(TableInfo table, RowEdit edit, List<DmlParameter> parameters)
    {
        if (edit.KeyValues.Count == 0)
            throw new InvalidOperationException(
                $"Не заданы ключевые значения строки таблицы «{table.Schema}.{table.Name}» — операция запрещена.");

        var predicates = new List<string>();
        foreach (var (name, value) in edit.KeyValues)
        {
            if (string.Equals(name, "ctid", StringComparison.OrdinalIgnoreCase))
            {
                // Адрес строки для таблиц без PK: параметр-текст приводится к типу tid.
                var p = $"k{parameters.Count}";
                predicates.Add($"ctid = CAST(@{p} AS tid)");
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
                predicates.Add($"{Quote(column.Name)} = {Placeholder(p, column.DataType)}");
                parameters.Add(new DmlParameter(p, value));
            }
        }
        return string.Join(" AND ", predicates);
    }

    /// <summary>Плейсхолдер с CAST к типу колонки: @s0 → CAST(@s0 AS integer).</summary>
    private static string Placeholder(string name, string? dataType) =>
        string.IsNullOrWhiteSpace(dataType) ? $"@{name}" : $"CAST(@{name} AS {dataType})";

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

/// <summary>DI-регистрация генератора DML PostgreSQL.</summary>
public static class PgDmlGeneratorExtensions
{
    /// <summary>Регистрирует <see cref="PgDmlGenerator"/> как <see cref="IDmlGenerator"/> (singleton).</summary>
    public static IServiceCollection AddPostgresDmlGeneration(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDmlGenerator, PgDmlGenerator>());
        return services;
    }
}
