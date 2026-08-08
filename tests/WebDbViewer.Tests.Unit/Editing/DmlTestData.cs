using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Editing;

/// <summary>Фабрики тестовых таблиц для тестов генерации DML.</summary>
internal static class DmlTestData
{
    /// <summary>Таблица с одноколоночным PK (нижний регистр — PostgreSQL-стиль).</summary>
    public static TableInfo PgUsers() => new()
    {
        Schema = "public",
        Name = "users",
        Type = DbObjectType.Table,
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new ColumnInfo { Name = "id", DataType = "integer", IsPrimaryKey = true, OrdinalPosition = 1 },
            new ColumnInfo { Name = "name", DataType = "text", IsNullable = true, OrdinalPosition = 2 },
            new ColumnInfo { Name = "age", DataType = "integer", IsNullable = true, OrdinalPosition = 3 },
            new ColumnInfo { Name = "full_name", DataType = "text", IsGenerated = true, OrdinalPosition = 4 },
        ],
    };

    /// <summary>Таблица без PK (PostgreSQL — идентификация по ctid).</summary>
    public static TableInfo PgNoPk() => new()
    {
        Schema = "public",
        Name = "logs",
        Type = DbObjectType.Table,
        Columns =
        [
            new ColumnInfo { Name = "message", DataType = "text", IsNullable = true, OrdinalPosition = 1 },
        ],
    };

    /// <summary>Таблица с составным PK и «опасными» идентификаторами (кавычки обязательны).</summary>
    public static TableInfo PgQuoted() => new()
    {
        Schema = "MySchema",
        Name = "Order Items",
        Type = DbObjectType.Table,
        PrimaryKeyColumns = ["Order Id", "select"],
        Columns =
        [
            new ColumnInfo { Name = "Order Id", DataType = "integer", IsPrimaryKey = true, OrdinalPosition = 1 },
            new ColumnInfo { Name = "select", DataType = "integer", IsPrimaryKey = true, OrdinalPosition = 2 },
            new ColumnInfo { Name = "Qty", DataType = "integer", IsNullable = true, OrdinalPosition = 3 },
        ],
    };

    /// <summary>Таблица с PK (верхний регистр — Oracle-стиль).</summary>
    public static TableInfo OraEmployees() => new()
    {
        Schema = "HR",
        Name = "EMPLOYEES",
        Type = DbObjectType.Table,
        PrimaryKeyColumns = ["EMPLOYEE_ID"],
        Columns =
        [
            new ColumnInfo { Name = "EMPLOYEE_ID", DataType = "NUMBER", IsPrimaryKey = true, OrdinalPosition = 1 },
            new ColumnInfo { Name = "LAST_NAME", DataType = "VARCHAR2", IsNullable = true, OrdinalPosition = 2 },
            new ColumnInfo { Name = "SALARY", DataType = "NUMBER", IsNullable = true, OrdinalPosition = 3 },
            new ColumnInfo { Name = "FULL_NAME", DataType = "VARCHAR2", IsGenerated = true, OrdinalPosition = 4 },
        ],
    };

    /// <summary>Таблица без PK (Oracle — идентификация по ROWID).</summary>
    public static TableInfo OraNoPk() => new()
    {
        Schema = "HR",
        Name = "AUDIT_LOG",
        Type = DbObjectType.Table,
        Columns =
        [
            new ColumnInfo { Name = "MESSAGE", DataType = "VARCHAR2", IsNullable = true, OrdinalPosition = 1 },
        ],
    };

    /// <summary>Oracle-таблица со смешанным регистром — идентификаторы должны квотироваться.</summary>
    public static TableInfo OraQuoted() => new()
    {
        Schema = "MyApp",
        Name = "Order Items",
        Type = DbObjectType.Table,
        PrimaryKeyColumns = ["Id"],
        Columns =
        [
            new ColumnInfo { Name = "Id", DataType = "NUMBER", IsPrimaryKey = true, OrdinalPosition = 1 },
            new ColumnInfo { Name = "Qty", DataType = "NUMBER", IsNullable = true, OrdinalPosition = 2 },
        ],
    };
}
