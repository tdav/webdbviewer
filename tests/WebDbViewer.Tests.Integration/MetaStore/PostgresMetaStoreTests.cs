using Microsoft.Extensions.Options;
using Npgsql;
using WebDbViewer.Core;
using WebDbViewer.Core.Security;
using WebDbViewer.Storage.Postgres;

namespace WebDbViewer.Tests.Integration.MetaStore;

/// <summary>
/// Интеграционные тесты метабазы на живом PostgreSQL.
/// Строка подключения берётся из переменной окружения WEBDBVIEWER_TEST_METASTORE,
/// иначе используется локальная по умолчанию. Если сервер недоступен — тесты пропускаются.
/// Каждый запуск работает в собственной схеме, которая удаляется по завершении.
/// </summary>
public sealed class PostgresMetaStoreTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=dbviewer_db;Username=postgres;Password=1;Pooling=true;";

    private readonly string connectionString =
        Environment.GetEnvironmentVariable("WEBDBVIEWER_TEST_METASTORE") ?? DefaultConnectionString;

    private readonly string schema = "wdv_test_" + Guid.NewGuid().ToString("N")[..12];

    private PostgresMetaStore? meta;
    private bool available;

    public async Task InitializeAsync()
    {
        try
        {
            await using var probe = new NpgsqlConnection(connectionString);
            await probe.OpenAsync();
            available = true;
        }
        catch (Exception)
        {
            available = false;
            return;
        }

        meta = new PostgresMetaStore(Options.Create(new PostgresStorageOptions
        {
            ConnectionString = connectionString,
            Schema = schema,
        }));
        await meta.EnsureCreatedAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (meta is not null)
        {
            await meta.DisposeAsync();

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [SkippableFact]
    public async Task Датасорс_сохраняется_и_читается_целиком()
    {
        Skip.IfNot(available);

        var store = new PostgresDataSourceStore(meta!);
        var config = new DataSourceConfig
        {
            Id = Guid.NewGuid(),
            Name = "Тестовое подключение",
            Kind = DbKind.Postgres,
            Host = "db.example.org",
            Port = 5433,
            Database = "app",
            Username = "reader",
            ProtectedPassword = "CfDJ8-зашифрованное",
            ReadOnly = true,
            IsProduction = true,
            UseSsl = true,
            ConnectTimeoutSeconds = 7,
            CommandTimeoutSeconds = 77,
            MaxPoolSizePerUser = 3,
            Extra = new Dictionary<string, string> { ["ApplicationName"] = "WebDbViewer" },
        };

        await store.SaveAsync(config, CancellationToken.None);
        var loaded = await store.GetAsync(config.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(config.Name, loaded!.Name);
        Assert.Equal(config.Kind, loaded.Kind);
        Assert.Equal(config.Port, loaded.Port);
        Assert.Equal(config.ProtectedPassword, loaded.ProtectedPassword);
        Assert.True(loaded.ReadOnly);
        Assert.True(loaded.IsProduction);
        Assert.True(loaded.UseSsl);
        Assert.Equal(7, loaded.ConnectTimeoutSeconds);
        Assert.Equal(77, loaded.CommandTimeoutSeconds);
        Assert.Equal(3, loaded.MaxPoolSizePerUser);
        Assert.Equal("WebDbViewer", loaded.Extra?["ApplicationName"]);

        await store.DeleteAsync(config.Id, CancellationToken.None);
        Assert.Null(await store.GetAsync(config.Id, CancellationToken.None));
    }

    [SkippableFact]
    public async Task Снапшот_схемы_перезаписывается_без_учёта_регистра()
    {
        Skip.IfNot(available);

        var store = new PostgresSnapshotStore(meta!);
        var dsId = Guid.NewGuid();

        await store.SaveAsync(dsId, Snapshot("public", "v1"), CancellationToken.None);
        await store.SaveAsync(dsId, Snapshot("PUBLIC", "v2"), CancellationToken.None);

        var loaded = await store.LoadAsync(dsId, "Public", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("v2", loaded!.VersionHash);

        var all = await store.LoadAllAsync(CancellationToken.None);
        Assert.Single(all.Where(s => s.DataSourceId == dsId));

        await store.DeleteAsync(dsId, null, CancellationToken.None);
        Assert.Null(await store.LoadAsync(dsId, "public", CancellationToken.None));
    }

    [SkippableFact]
    public async Task Аудит_фильтруется_по_пользователю_и_периоду()
    {
        Skip.IfNot(available);

        var auditor = new PostgresQueryAuditor(meta!);
        var dsId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await auditor.RecordAsync(Entry(dsId, "admin", now.AddMinutes(-1)), CancellationToken.None);
        await auditor.RecordAsync(Entry(dsId, "guest", now.AddMinutes(-2)), CancellationToken.None);

        var mine = await auditor.QueryAsync(
            now.AddHours(-1), now.AddMinutes(1), "admin", dsId, 50, CancellationToken.None);

        Assert.Single(mine);
        Assert.Equal("admin", mine[0].UserName);
        Assert.True(mine[0].Success);
        Assert.Equal(TimeSpan.FromMilliseconds(42), mine[0].Duration);
    }

    [SkippableFact]
    public async Task Первая_учётная_запись_создаётся_один_раз()
    {
        Skip.IfNot(available);

        var users = new PostgresUserStore(meta!);
        Assert.Equal(0, await users.CountAsync(CancellationToken.None));

        var user = new AppUser
        {
            Username = "Admin",
            PasswordHash = "PBKDF2-SHA256$100000$c2FsdA==$aGFzaA==",
            MustChangePassword = true,
        };

        Assert.True(await users.TryCreateAsync(user, CancellationToken.None));
        Assert.False(await users.TryCreateAsync(user with { PasswordHash = "другой" }, CancellationToken.None));

        var found = await users.FindAsync("ADMIN", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("admin", found!.Username);
        Assert.Equal(user.PasswordHash, found.PasswordHash);
        Assert.True(found.MustChangePassword);
        Assert.Equal(1, await users.CountAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Ключи_Data_Protection_сохраняются_и_читаются()
    {
        Skip.IfNot(available);

        var repository = new PostgresXmlRepository(meta!);
        repository.StoreElement(new System.Xml.Linq.XElement("key", new System.Xml.Linq.XAttribute("id", "k1")), "key-1");

        var elements = repository.GetAllElements();
        Assert.Single(elements);
        Assert.Equal("k1", elements.Single().Attribute("id")?.Value);

        await Task.CompletedTask;
    }

    private static SchemaSnapshot Snapshot(string name, string version) => new()
    {
        SchemaName = name,
        Tables = [],
        VersionHash = version,
        LoadedAt = DateTimeOffset.UtcNow,
    };

    private static AuditEntry Entry(Guid dsId, string user, DateTimeOffset at) => new()
    {
        UserName = user,
        DataSourceId = dsId,
        SqlText = "SELECT 1",
        StartedAt = at,
        Duration = TimeSpan.FromMilliseconds(42),
        RowsAffected = 1,
        Success = true,
        ClientIp = "::1",
    };
}
