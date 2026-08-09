using WebDbViewer.Core;
using WebDbViewer.Core.Sessions;

namespace WebDbViewer.Tests.Unit.Connections;

/// <summary>Тесты файлового JSON-хранилища датасорсов.</summary>
public class DataSourceFileStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdbv-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "datasources.json");

    private static DataSourceConfig NewConfig(string name = "pg-local") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = DbKind.Postgres,
        Host = "localhost",
        Port = 5432,
        Database = "demo",
        Username = "app",
        ProtectedPassword = "CfDJ8-protected-blob",
        UseSsl = true,
        Extra = new Dictionary<string, string> { ["Search Path"] = "public" },
    };

    [Fact]
    public async Task SaveAndGet_RoundTripsAllFields()
    {
        using var store = new DataSourceFileStore(FilePath);
        var config = NewConfig();

        await store.SaveAsync(config, CancellationToken.None);
        var loaded = await store.GetAsync(config.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(config.Name, loaded.Name);
        Assert.Equal(config.Kind, loaded.Kind);
        Assert.Equal(config.ProtectedPassword, loaded.ProtectedPassword);
        Assert.Equal("public", loaded.Extra?["Search Path"]);
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsAllowAllSchemas()
    {
        using var store = new DataSourceFileStore(FilePath);
        var restricted = NewConfig("pg-restricted") with { AllowAllSchemas = false };

        await store.SaveAsync(restricted, CancellationToken.None);
        var loaded = await store.GetAsync(restricted.Id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.False(loaded.AllowAllSchemas);
    }

    [Fact]
    public async Task Load_LegacyJsonWithoutAllowAllSchemas_DefaultsToTrue()
    {
        // Датасорсы, сохранённые до появления флага, должны сохранить прежнее поведение.
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, $$"""
            [{"id":"{{id}}","name":"legacy","kind":"Postgres","host":"localhost","port":5432,
              "database":"demo","username":"app"}]
            """);

        using var store = new DataSourceFileStore(FilePath);
        var loaded = await store.GetAsync(id, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.True(loaded.AllowAllSchemas);
    }

    [Fact]
    public async Task Persistence_SurvivesStoreRecreation()
    {
        var config = NewConfig();
        using (var store = new DataSourceFileStore(FilePath))
        {
            await store.SaveAsync(config, CancellationToken.None);
        }

        using var reopened = new DataSourceFileStore(FilePath);
        var loaded = await reopened.GetAsync(config.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(config.Name, loaded.Name);
    }

    [Fact]
    public async Task Save_ExistingId_Overwrites()
    {
        using var store = new DataSourceFileStore(FilePath);
        var config = NewConfig();
        await store.SaveAsync(config, CancellationToken.None);
        await store.SaveAsync(config with { Name = "renamed" }, CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("renamed", all[0].Name);
    }

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        using var store = new DataSourceFileStore(FilePath);
        var config = NewConfig();
        await store.SaveAsync(config, CancellationToken.None);

        await store.DeleteAsync(config.Id, CancellationToken.None);

        Assert.Null(await store.GetAsync(config.Id, CancellationToken.None));
        Assert.Empty(await store.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetAll_MissingFile_ReturnsEmpty()
    {
        using var store = new DataSourceFileStore(FilePath);
        Assert.Empty(await store.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StoredJson_ContainsOnlyProtectedPassword()
    {
        using var store = new DataSourceFileStore(FilePath);
        await store.SaveAsync(NewConfig(), CancellationToken.None);

        var json = await File.ReadAllTextAsync(FilePath);
        // В файле — только защищённый blob, без открытого пароля.
        Assert.Contains("CfDJ8-protected-blob", json);
        Assert.DoesNotContain("plainPassword", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentSaves_AllPersisted()
    {
        using var store = new DataSourceFileStore(FilePath);
        var configs = Enumerable.Range(0, 20).Select(i => NewConfig($"ds-{i:D2}")).ToList();

        await Task.WhenAll(configs.Select(c => store.SaveAsync(c, CancellationToken.None)));

        var all = await store.GetAllAsync(CancellationToken.None);
        Assert.Equal(20, all.Count);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // очистка temp — best effort
        }
    }
}
