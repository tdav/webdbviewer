using System.Data.Common;

namespace WebDbViewer.Core.Editing;

/// <summary>Именованный параметр сгенерированного DML (имя без диалектного префикса @/:).</summary>
public sealed record DmlParameter(string Name, object? Value);

/// <summary>
/// Результат генерации одного DML-оператора: параметризованный SQL + значения параметров.
/// Значения НИКОГДА не конкатенируются в текст — только через <see cref="DbParameter"/>.
/// </summary>
public sealed record DmlCommand
{
    public required string Sql { get; init; }
    public required IReadOnlyList<DmlParameter> Parameters { get; init; }

    /// <summary>
    /// Создаёт <see cref="DbCommand"/> с заполненными <see cref="DbParameter"/>.
    /// Для Oracle включает BindByName (если провайдер поддерживает это свойство),
    /// параметры добавляются в порядке появления в SQL — позиционное связывание тоже корректно.
    /// </summary>
    public DbCommand CreateDbCommand(DbConnection connection)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;

        // ODP.NET по умолчанию связывает параметры по позиции; включаем связывание по имени.
        var bindByName = cmd.GetType().GetProperty("BindByName");
        if (bindByName is not null && bindByName.PropertyType == typeof(bool))
            bindByName.SetValue(cmd, true);

        foreach (var parameter in Parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = parameter.Name;
            p.Value = parameter.Value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
