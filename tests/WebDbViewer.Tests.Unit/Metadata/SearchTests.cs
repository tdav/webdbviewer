using Microsoft.Extensions.Options;
using WebDbViewer.Core;
using WebDbViewer.Metadata;
using WebDbViewer.Metadata.Search;

namespace WebDbViewer.Tests.Unit.Metadata;

/// <summary>Тесты префиксного trie и поиска с ранжированием.</summary>
public class SearchTests
{
    [Fact]
    public void Trie_PrefixLookup_IgnoresCase_AndReturnsSubtree()
    {
        var trie = new PrefixTrie<string>();
        trie.Add("user_table", "user_table");
        trie.Add("USER_ROLES", "USER_ROLES");
        trie.Add("orders", "orders");

        var found = trie.GetByPrefix("useR").ToList();
        Assert.Equal(2, found.Count);
        Assert.Contains("user_table", found);
        Assert.Contains("USER_ROLES", found); // оригинальное имя сохранено
        Assert.Empty(trie.GetByPrefix("xyz"));
    }

    private static MetadataCache CreateCache(FakeLoader loader, InMemorySnapshotStore store)
        => new(loader, store, Options.Create(new MetadataCacheOptions { Ttl = TimeSpan.FromHours(1) }));

    [Fact]
    public async Task Search_RanksPrefixAboveHumpsAboveSubstring()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader
        {
            Factory = (_, _, schema) => TestHelpers.Snapshot(schema, null,
                TestHelpers.Table(schema, "get_price"),      // humps для "gp"
                TestHelpers.Table(schema, "gp_summary"),     // точный префикс "gp"
                TestHelpers.Table(schema, "big_pump"))       // ни префикс, ни humps ("bigp..." содержит "gp"? нет)
        };
        var cache = CreateCache(loader, new InMemorySnapshotStore());
        await cache.GetSchemaAsync(ds, null, "public", CancellationToken.None);

        var results = await cache.SearchAsync(ds, "gp", 10, CancellationToken.None);
        var names = results.Select(r => r.Name).ToList();

        Assert.Equal("gp_summary", names[0]);          // префикс — выше всех
        Assert.Contains("get_price", names);           // humps-совпадение найдено
        Assert.True(names.IndexOf("gp_summary") < names.IndexOf("get_price"));
    }

    [Fact]
    public async Task Search_HumpsFindsUnderscoreAndCamelCase()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader
        {
            Factory = (_, _, schema) => TestHelpers.Snapshot(schema, null,
                TestHelpers.Table(schema, "user_table"),
                TestHelpers.Table(schema, "orders"))
        };
        var cache = CreateCache(loader, new InMemorySnapshotStore());
        await cache.GetSchemaAsync(ds, null, "public", CancellationToken.None);

        var byHumps = await cache.SearchAsync(ds, "usrTbl", 10, CancellationToken.None);
        Assert.Contains(byHumps, n => n.Name == "user_table");

        var none = await cache.SearchAsync(ds, "zzz", 10, CancellationToken.None);
        Assert.Empty(none);
    }

    [Fact]
    public async Task Search_TablesWithForeignKeys_RankHigher()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader
        {
            // Имена одинаковой длины: различие в ранге даёт только FK-бонус
            Factory = (_, _, schema) => TestHelpers.Snapshot(schema, null,
                TestHelpers.Table(schema, "acc_data1", withFk: false),
                TestHelpers.Table(schema, "acc_data2", withFk: true))
        };
        var cache = CreateCache(loader, new InMemorySnapshotStore());
        await cache.GetSchemaAsync(ds, null, "public", CancellationToken.None);

        var results = await cache.SearchAsync(ds, "acc", 10, CancellationToken.None);
        Assert.Equal("acc_data2", results[0].Name); // таблица с FK выше
    }

    [Fact]
    public async Task Search_FindsColumns_WithTableAttribute()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader
        {
            Factory = (_, _, schema) => TestHelpers.Snapshot(schema, null,
                TestHelpers.Table(schema, "users", ["id", "email_address"]))
        };
        var cache = CreateCache(loader, new InMemorySnapshotStore());
        await cache.GetSchemaAsync(ds, null, "public", CancellationToken.None);

        var results = await cache.SearchAsync(ds, "email", 10, CancellationToken.None);
        var column = Assert.Single(results, n => n.Type == DbObjectType.Column);
        Assert.Equal("email_address", column.Name);
        Assert.Equal("users", column.Attributes?["table"]);
    }

    [Fact]
    public async Task Search_RespectsLimit()
    {
        var ds = Guid.NewGuid();
        var loader = new FakeLoader
        {
            Factory = (_, _, schema) => TestHelpers.Snapshot(schema, null,
                Enumerable.Range(1, 20).Select(i => TestHelpers.Table(schema, $"tbl_{i:D2}")).ToArray())
        };
        var cache = CreateCache(loader, new InMemorySnapshotStore());
        await cache.GetSchemaAsync(ds, null, "public", CancellationToken.None);

        var results = await cache.SearchAsync(ds, "tbl", 5, CancellationToken.None);
        Assert.Equal(5, results.Count);
    }
}
