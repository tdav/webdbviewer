using System.Text.Encodings.Web;
using System.Text.Json;
using WebDbViewer.Core;
using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Unit.Completion;

public class SchemaMapDtoTests
{
    // ponytail: JsonSerializerOptions.Default escapes non-ASCII as \uXXXX (verified against
    // net10.0), so the literal Cyrillic assertion below needs a relaxed encoder or it never
    // matches regardless of DTO correctness. Only this test touches non-ASCII text.
    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static SchemaSnapshot Snapshot(int tables, int columnsPerTable, string? versionHash = "v1") => new()
    {
        SchemaName = "public",
        Tables = Enumerable.Range(0, tables).Select(i => new TableInfo
        {
            Schema = "public",
            Name = "t" + i,
            Type = DbObjectType.Table,
            Columns = Enumerable.Range(0, columnsPerTable).Select(c => new ColumnInfo
            {
                Name = "c" + c,
                DataType = "text",
                OrdinalPosition = c + 1,
                IsNullable = true,
            }).ToList(),
        }).ToList(),
        LoadedAt = DateTimeOffset.UtcNow,
        VersionHash = versionHash,
    };

    [Fact]
    public void From_UsesShortKeys()
    {
        var dto = SchemaMapDto.From(SnapshotWithComment());
        var json = JsonSerializer.Serialize(dto, RelaxedJson);

        Assert.Contains("\"n\":\"users\"", json);
        Assert.Contains("\"t\":\"table\"", json);
        Assert.Contains("\"pk\":true", json);
        Assert.Contains("\"cm\":\"Пользователи\"", json);
    }

    [Fact]
    public void From_OmitsEmptyComment()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: 1, columnsPerTable: 1));
        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("\"cm\"", json);
    }

    [Fact]
    public void From_DropsColumnsWhenTooManyTables()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: SchemaMapDto.MaxTables + 1, columnsPerTable: 1));

        Assert.True(dto.Partial);
        Assert.All(dto.Tables, t => Assert.Empty(t.Columns));
    }

    [Fact]
    public void From_DropsColumnsWhenTooManyColumns()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: 100, columnsPerTable: 501)); // 50 100 > 50 000

        Assert.True(dto.Partial);
        Assert.All(dto.Tables, t => Assert.Empty(t.Columns));
    }

    [Fact]
    public void From_KeepsColumnsAtThreshold()
    {
        var dto = SchemaMapDto.From(Snapshot(tables: SchemaMapDto.MaxTables, columnsPerTable: 1));

        Assert.False(dto.Partial);
        Assert.All(dto.Tables, t => Assert.Single(t.Columns));
    }

    [Theory]
    [InlineData("\"v1\"", true)]
    [InlineData("\"v2\"", false)]
    [InlineData(null, false)]
    public void IsNotModified_ComparesETag(string? ifNoneMatch, bool expected)
    {
        var etag = SchemaMapDto.ETagFor(Snapshot(1, 1));

        Assert.Equal(expected, SchemaMapDto.IsNotModified(etag, ifNoneMatch));
    }

    [Fact]
    public void IsNotModified_FalseWhenNoVersionHash()
    {
        var etag = SchemaMapDto.ETagFor(Snapshot(1, 1, versionHash: null));

        Assert.Null(etag);
        Assert.False(SchemaMapDto.IsNotModified(etag, "\"anything\""));
    }

    private static SchemaSnapshot SnapshotWithComment() => new()
    {
        SchemaName = "public",
        Tables =
        [
            new TableInfo
            {
                Schema = "public",
                Name = "users",
                Type = DbObjectType.Table,
                Comment = "Пользователи",
                Columns = [new ColumnInfo { Name = "id", DataType = "bigint", OrdinalPosition = 1, IsPrimaryKey = true }],
                PrimaryKeyColumns = ["id"],
            },
        ],
        LoadedAt = DateTimeOffset.UtcNow,
        VersionHash = "v1",
    };
}
