namespace WebDbViewer.Core;

/// <summary>Тип СУБД.</summary>
public enum DbKind { Postgres, Oracle }

/// <summary>Конфигурация датасорса (подключения). Credentials хранятся в зашифрованном виде.</summary>
public sealed record DataSourceConfig
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required DbKind Kind { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; }
    public required string Database { get; init; }
    public required string Username { get; init; }
    /// <summary>Зашифрованный пароль (Data Protection API).</summary>
    public string? ProtectedPassword { get; init; }
    public bool ReadOnly { get; init; }
    public bool IsProduction { get; init; }
    public bool UseSsl { get; init; }
    public int ConnectTimeoutSeconds { get; init; } = 15;
    public int CommandTimeoutSeconds { get; init; } = 120;
    public int MaxPoolSizePerUser { get; init; } = 5;
    /// <summary>Дополнительные параметры строки подключения.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}

/// <summary>Тип объекта БД в навигаторе/кэше.</summary>
public enum DbObjectType
{
    Database, Schema, Table, View, MaterializedView, Sequence, Function, Procedure,
    Package, Type, Extension, Index, Trigger, Synonym, DbLink, Tablespace, Column, Constraint
}

/// <summary>Узел дерева объектов.</summary>
public sealed record DbObjectNode
{
    public required string Name { get; init; }
    public required DbObjectType Type { get; init; }
    public string? Schema { get; init; }
    public string? Comment { get; init; }
    public bool HasChildren { get; init; }
    public bool IsSystem { get; init; }
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}

public sealed record ColumnInfo
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsGenerated { get; init; }
    public string? DefaultValue { get; init; }
    public string? Comment { get; init; }
    public int OrdinalPosition { get; init; }
}

public sealed record ForeignKeyInfo
{
    public required string ConstraintName { get; init; }
    public required string FromSchema { get; init; }
    public required string FromTable { get; init; }
    public required IReadOnlyList<string> FromColumns { get; init; }
    public required string ToSchema { get; init; }
    public required string ToTable { get; init; }
    public required IReadOnlyList<string> ToColumns { get; init; }
}

public sealed record TableInfo
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required DbObjectType Type { get; init; }
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public IReadOnlyList<ForeignKeyInfo> ForeignKeys { get; init; } = [];
    public IReadOnlyList<string> PrimaryKeyColumns { get; init; } = [];
    public string? Comment { get; init; }
}

/// <summary>Снапшот схемы для кэша метаданных.</summary>
public sealed record SchemaSnapshot
{
    public required string SchemaName { get; init; }
    public required IReadOnlyList<TableInfo> Tables { get; init; }
    public IReadOnlyList<RoutineInfo> Routines { get; init; } = [];
    public DateTimeOffset LoadedAt { get; init; }
    public string? VersionHash { get; init; }
}

public sealed record RoutineInfo
{
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required DbObjectType Type { get; init; }
    public string? ReturnType { get; init; }
    public string? ArgumentsSignature { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Результат выполнения одного statement.</summary>
public sealed record QueryResultMeta
{
    public required IReadOnlyList<ColumnInfo> Columns { get; init; }
    public long? AffectedRows { get; init; }
    public TimeSpan Elapsed { get; init; }
    public bool Truncated { get; init; }
    public string? StatementKind { get; init; }
}

/// <summary>Строка результата: значения уже приведены к отображаемому виду (null = NULL).</summary>
public sealed record ResultRow(IReadOnlyList<object?> Values);

/// <summary>Постраничный запрос данных таблицы (keyset-пагинация).</summary>
public sealed record DataPageRequest
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public int Limit { get; init; } = 200;
    /// <summary>Значения ключа последней строки предыдущей страницы (keyset).</summary>
    public IReadOnlyList<object?>? After { get; init; }
    public string? OrderBy { get; init; }
    public bool OrderDescending { get; init; }
    public string? WhereFilter { get; init; }
}

/// <summary>Запись аудита выполненного запроса.</summary>
public sealed record AuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string UserName { get; init; }
    public required Guid DataSourceId { get; init; }
    public required string SqlText { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public long? RowsAffected { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ClientIp { get; init; }
}
