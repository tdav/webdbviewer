using WebDbViewer.Core;
using WebDbViewer.Web.Services;

namespace WebDbViewer.Tests.Unit.Connections;

/// <summary>Тесты области видимости схем датасорса (флаг AllowAllSchemas).</summary>
public class SchemaScopeTests
{
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(["app"], StringComparer.OrdinalIgnoreCase);

    private static DbObjectNode Schema(string name) => new() { Name = name, Type = DbObjectType.Schema };

    private static DbObjectNode Table(string name, string? schema) =>
        new() { Name = name, Type = DbObjectType.Table, Schema = schema };

    [Fact]
    public void IsAllowed_NoRestriction_AllowsAnySchema()
    {
        Assert.True(SchemaScope.IsAllowed(null, "anything"));
        Assert.True(SchemaScope.IsAllowed(null, null));
    }

    [Fact]
    public void IsAllowed_Restricted_AllowsOnlyListedSchema()
    {
        Assert.True(SchemaScope.IsAllowed(Allowed, "app"));
        Assert.True(SchemaScope.IsAllowed(Allowed, "APP")); // сравнение без учёта регистра
        Assert.False(SchemaScope.IsAllowed(Allowed, "public"));
        Assert.False(SchemaScope.IsAllowed(Allowed, null));
    }

    [Fact]
    public void Filter_NoRestriction_ReturnsSourceUnchanged()
    {
        var nodes = new[] { Schema("app"), Schema("public") };
        Assert.Same(nodes, SchemaScope.Filter(null, nodes));
    }

    [Fact]
    public void Filter_Restricted_KeepsSchemaNodesByName()
    {
        var filtered = SchemaScope.Filter(Allowed, [Schema("app"), Schema("public"), Schema("audit")]);

        Assert.Equal(["app"], filtered.Select(n => n.Name));
    }

    [Fact]
    public void Filter_Restricted_KeepsObjectNodesByOwningSchema()
    {
        var filtered = SchemaScope.Filter(Allowed,
            [Table("orders", "app"), Table("orders", "public"), Table("orphan", null)]);

        var only = Assert.Single(filtered);
        Assert.Equal("app", only.Schema);
    }
}
