using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Connections;

/// <summary>Fake-соединение: открывается без сервера, отслеживает состояние.</summary>
internal sealed class FakeDbConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    public bool Disposed { get; private set; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => new FakeDbTransaction(this, isolationLevel);

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        _state = ConnectionState.Closed;
        base.Dispose(disposing);
    }
}

internal sealed class FakeDbTransaction : DbTransaction
{
    private readonly FakeDbConnection _connection;

    public FakeDbTransaction(FakeDbConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
    }

    public bool Committed { get; private set; }
    public bool RolledBack { get; private set; }

    public override IsolationLevel IsolationLevel { get; }
    protected override DbConnection? DbConnection => _connection;

    public override void Commit() => Committed = true;
    public override void Rollback() => RolledBack = true;
}

/// <summary>Fake-провайдер: возвращает открытые fake-соединения, считает открытия.</summary>
internal sealed class FakeDbProvider : IDbProvider
{
    public int OpenCount;
    public List<FakeDbConnection> OpenedConnections { get; } = [];

    public DbKind Kind => DbKind.Postgres;
    public bool SupportsDatabaseLevel => true;
    public string? RowAddressPseudoColumn => null;

    /// <summary>Базы, к которым открывались соединения (в порядке открытия).</summary>
    public List<string> OpenedDatabases { get; } = [];

    public string BuildConnectionString(DataSourceConfig config, string plainPassword) => "fake";

    public Task<DbConnection> OpenConnectionAsync(DataSourceConfig config, string plainPassword, CancellationToken ct)
    {
        Interlocked.Increment(ref OpenCount);
        lock (OpenedDatabases)
            OpenedDatabases.Add(config.Database);
        var connection = new FakeDbConnection();
        connection.Open();
        lock (OpenedConnections)
            OpenedConnections.Add(connection);
        return Task.FromResult<DbConnection>(connection);
    }

    public Task<string> TestConnectionAsync(DataSourceConfig config, string plainPassword, CancellationToken ct)
        => Task.FromResult("Fake 1.0");

    public Task<IReadOnlyList<DbObjectNode>> GetChildrenAsync(DbConnection connection, IReadOnlyList<string> parentPath, bool includeSystem, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> GetSchemasAsync(DbConnection connection, bool includeSystem, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<DbObjectNode>> GetDatabasesAsync(DbConnection connection, bool includeSystem, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<SchemaSnapshot> LoadSchemaSnapshotAsync(DbConnection connection, string schemaName, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<TableInfo> GetTableInfoAsync(DbConnection connection, string schema, string table, CancellationToken ct)
        => throw new NotSupportedException();

    public Task<string> GetSchemaVersionAsync(DbConnection connection, string schemaName, CancellationToken ct)
        => throw new NotSupportedException();

    public string QuoteIdentifier(string identifier) => identifier;

    public string BuildSelectPageSql(TableInfo table, DataPageRequest request) => "SELECT 1";
}

/// <summary>In-memory хранилище датасорсов для тестов.</summary>
internal sealed class InMemoryDataSourceStore : IDataSourceStore
{
    private readonly Dictionary<Guid, DataSourceConfig> _items = [];

    public void Add(DataSourceConfig config) => _items[config.Id] = config;

    public Task<IReadOnlyList<DataSourceConfig>> GetAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DataSourceConfig>>(_items.Values.ToList());

    public Task<DataSourceConfig?> GetAsync(Guid id, CancellationToken ct)
        => Task.FromResult(_items.TryGetValue(id, out var c) ? c : null);

    public Task SaveAsync(DataSourceConfig config, CancellationToken ct)
    {
        _items[config.Id] = config;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct)
    {
        _items.Remove(id);
        return Task.CompletedTask;
    }
}

/// <summary>Простейший «протектор» для тестов: reverse строки.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => new(plaintext.Reverse().ToArray());
    public string Unprotect(string ciphertext) => new(ciphertext.Reverse().ToArray());
}
