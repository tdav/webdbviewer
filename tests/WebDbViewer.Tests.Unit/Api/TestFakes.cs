using System.Data;
using System.Data.Common;
using WebDbViewer.Core;

namespace WebDbViewer.Tests.Unit.Api;

/// <summary>Минимальная фейковая сессия БД для тестов реестра выполнений.</summary>
internal sealed class FakeDbSession : IDbSession
{
    public int CancelRunningCalls;

    public Guid SessionId { get; } = Guid.NewGuid();
    public Guid DataSourceId { get; init; } = Guid.NewGuid();
    public string UserName { get; init; } = "tester";
    public string Database { get; init; } = "demo";
    public bool IsPrimary { get; init; } = true;
    public DbConnection Connection { get; } = new FakeDbConnection();
    public bool AutoCommit { get; set; } = true;
    public bool InTransaction => false;
    public DateTimeOffset LastUsedAt => DateTimeOffset.UtcNow;

    public Task BeginTransactionAsync(CancellationToken ct) => Task.CompletedTask;
    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;
    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public void CancelRunning() => Interlocked.Increment(ref CancelRunningCalls);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Пустое DbConnection-заглушка (никогда не открывается в юнит-тестах).</summary>
internal sealed class FakeDbConnection : DbConnection
{
    public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0.0";
    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}
