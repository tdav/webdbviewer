using Microsoft.Extensions.Options;
using WebDbViewer.Core;
using WebDbViewer.Core.Sessions;

namespace WebDbViewer.Tests.Unit.Connections;

/// <summary>Тесты менеджера stateful-сессий: лимиты, переиспользование, TTL, закрытие.</summary>
public class DbSessionManagerTests
{
    private static DataSourceConfig NewConfig(Guid? id = null, string? protectedPassword = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "test",
        Kind = DbKind.Postgres,
        Host = "localhost",
        Database = "db",
        Username = "u",
        ProtectedPassword = protectedPassword,
    };

    private static (DbSessionManager Manager, InMemoryDataSourceStore Store, FakeDbProvider Provider) CreateManager(
        DbSessionOptions? options = null, ISecretProtector? protector = null)
    {
        var store = new InMemoryDataSourceStore();
        var provider = new FakeDbProvider();
        var registry = new DbProviderRegistry([provider]);
        var manager = new DbSessionManager(store, registry, protector, Options.Create(options ?? new DbSessionOptions()));
        return (manager, store, provider);
    }

    [Fact]
    public async Task GetOrCreate_OpensConnection_AndRegistersSession()
    {
        var (manager, store, provider) = CreateManager();
        var config = NewConfig();
        store.Add(config);

        var session = await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);

        Assert.Equal(config.Id, session.DataSourceId);
        Assert.Equal("ivan", session.UserName);
        Assert.True(session.AutoCommit);
        Assert.False(session.InTransaction);
        Assert.Equal(1, provider.OpenCount);
        Assert.Single(await manager.ListForUserAsync("ivan", CancellationToken.None));
    }

    [Fact]
    public async Task GetOrCreate_SameUserAndDataSource_ReusesSession()
    {
        var (manager, store, provider) = CreateManager();
        var config = NewConfig();
        store.Add(config);

        var s1 = await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);
        var s2 = await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);

        Assert.Same(s1, s2);
        Assert.Equal(1, provider.OpenCount);
    }

    [Fact]
    public async Task GetOrCreate_ExceedsPerUserLimit_Throws()
    {
        var (manager, store, _) = CreateManager(new DbSessionOptions { MaxSessionsPerUser = 2 });
        var configs = Enumerable.Range(0, 3).Select(_ => NewConfig()).ToList();
        foreach (var c in configs)
            store.Add(c);

        await manager.GetOrCreateAsync("ivan", configs[0].Id, CancellationToken.None);
        await manager.GetOrCreateAsync("ivan", configs[1].Id, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.GetOrCreateAsync("ivan", configs[2].Id, CancellationToken.None));
        Assert.Contains("лимит", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrCreate_LimitIsPerUser_NotGlobal()
    {
        var (manager, store, _) = CreateManager(new DbSessionOptions { MaxSessionsPerUser = 1 });
        var c1 = NewConfig();
        var c2 = NewConfig();
        store.Add(c1);
        store.Add(c2);

        await manager.GetOrCreateAsync("ivan", c1.Id, CancellationToken.None);
        // Другой пользователь — свой лимит.
        var other = await manager.GetOrCreateAsync("petr", c2.Id, CancellationToken.None);

        Assert.Equal("petr", other.UserName);
    }

    [Fact]
    public async Task Close_FreesSlot_AndDisposesConnection()
    {
        var (manager, store, provider) = CreateManager(new DbSessionOptions { MaxSessionsPerUser = 1 });
        var c1 = NewConfig();
        var c2 = NewConfig();
        store.Add(c1);
        store.Add(c2);

        var s1 = await manager.GetOrCreateAsync("ivan", c1.Id, CancellationToken.None);
        await manager.CloseAsync(s1.SessionId, CancellationToken.None);

        Assert.True(provider.OpenedConnections[0].Disposed);
        Assert.Null(await manager.FindAsync(s1.SessionId, CancellationToken.None));

        // Слот освободился — можно открыть новую сессию.
        var s2 = await manager.GetOrCreateAsync("ivan", c2.Id, CancellationToken.None);
        Assert.NotEqual(s1.SessionId, s2.SessionId);
    }

    [Fact]
    public async Task Find_UnknownSession_ReturnsNull()
    {
        var (manager, _, _) = CreateManager();
        Assert.Null(await manager.FindAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetOrCreate_UnknownDataSource_Throws()
    {
        var (manager, _, _) = CreateManager();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.GetOrCreateAsync("ivan", Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ProtectedPassword_WithoutProtector_Throws()
    {
        var (manager, store, _) = CreateManager(protector: null);
        var config = NewConfig(protectedPassword: "abc");
        store.Add(config);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None));
        Assert.Contains("ISecretProtector", ex.Message);
    }

    [Fact]
    public async Task ProtectedPassword_WithProtector_Succeeds()
    {
        var (manager, store, _) = CreateManager(protector: new FakeSecretProtector());
        var config = NewConfig(protectedPassword: new FakeSecretProtector().Protect("secret"));
        store.Add(config);

        var session = await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);
        Assert.NotNull(session);
    }

    [Fact]
    public async Task Sweep_ClosesExpiredSessions()
    {
        var (manager, store, provider) = CreateManager(new DbSessionOptions { IdleTtl = TimeSpan.FromMilliseconds(1) });
        var config = NewConfig();
        store.Add(config);

        var session = await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);
        await Task.Delay(50);

        var closed = await manager.SweepExpiredAsync();

        Assert.Equal(1, closed);
        Assert.True(provider.OpenedConnections[0].Disposed);
        Assert.Null(await manager.FindAsync(session.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Sweep_KeepsFreshSessions()
    {
        var (manager, store, _) = CreateManager(new DbSessionOptions { IdleTtl = TimeSpan.FromHours(1) });
        var config = NewConfig();
        store.Add(config);

        var session = await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);
        var closed = await manager.SweepExpiredAsync();

        Assert.Equal(0, closed);
        Assert.NotNull(await manager.FindAsync(session.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeAsync_ClosesAllSessions()
    {
        var (manager, store, provider) = CreateManager();
        var config = NewConfig();
        store.Add(config);
        await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);

        await manager.DisposeAsync();

        Assert.True(provider.OpenedConnections[0].Disposed);
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    [Fact]
    public async Task Session_TransactionLifecycle()
    {
        var (manager, store, _) = CreateManager();
        var config = NewConfig();
        store.Add(config);

        var session = await manager.GetOrCreateAsync("ivan", config.Id, CancellationToken.None);

        await session.BeginTransactionAsync(CancellationToken.None);
        Assert.True(session.InTransaction);
        // Повторный Begin запрещён.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginTransactionAsync(CancellationToken.None));

        await session.CommitAsync(CancellationToken.None);
        Assert.False(session.InTransaction);

        // Commit без транзакции запрещён.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Session_DisposeRollsBackOpenTransaction()
    {
        var provider = new FakeDbProvider();
        var connection = (FakeDbConnection)await provider.OpenConnectionAsync(NewConfig(), "", CancellationToken.None);
        var session = new DbSession(Guid.NewGuid(), "ivan", connection);

        await session.BeginTransactionAsync(CancellationToken.None);
        await session.DisposeAsync();

        Assert.True(connection.Disposed);
        // Повторное использование после Dispose запрещено.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.BeginTransactionAsync(CancellationToken.None));
    }
}
