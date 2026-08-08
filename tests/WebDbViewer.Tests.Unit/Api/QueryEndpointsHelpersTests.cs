using System.Text.Json;
using WebDbViewer.Core;
using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Unit.Api;

/// <summary>Тесты вспомогательной логики endpoints: приведение значений и keyset-ключи.</summary>
public class QueryEndpointsHelpersTests
{
    // ---------------- FormatValue: приведение значений к JSON-сериализуемому виду ----------------

    [Fact]
    public void FormatValue_DBNull_ПревращаетсяВNull()
    {
        Assert.Null(QueryEndpoints.FormatValue(DBNull.Value));
        Assert.Null(QueryEndpoints.FormatValue(null));
    }

    [Fact]
    public void FormatValue_Примитивы_БезИзменений()
    {
        Assert.Equal(42, QueryEndpoints.FormatValue(42));
        Assert.Equal("текст", QueryEndpoints.FormatValue("текст"));
        Assert.Equal(true, QueryEndpoints.FormatValue(true));
        Assert.Equal(3.5m, QueryEndpoints.FormatValue(3.5m));
    }

    [Fact]
    public void FormatValue_DateTime_ФорматируетсяСтрокой()
    {
        var value = QueryEndpoints.FormatValue(new DateTime(2026, 8, 8, 12, 30, 45, 123));

        Assert.Equal("2026-08-08 12:30:45.123", value);
    }

    [Fact]
    public void FormatValue_МассивБайтов_HexПревью()
    {
        var value = QueryEndpoints.FormatValue(new byte[] { 0xDE, 0xAD });

        Assert.Equal("0xDEAD", value);
    }

    [Fact]
    public void FormatValue_НеизвестныйТип_Строкой()
    {
        var value = QueryEndpoints.FormatValue(new Uri("http://localhost/x"));

        Assert.IsType<string>(value);
    }

    // ---------------- FromJsonElement: параметры keyset-пагинации ----------------

    [Fact]
    public void FromJsonElement_Примитивы()
    {
        Assert.Equal(123L, QueryEndpoints.FromJsonElement(JsonSerializer.Deserialize<JsonElement>("123")));
        Assert.Equal("abc", QueryEndpoints.FromJsonElement(JsonSerializer.Deserialize<JsonElement>("\"abc\"")));
        Assert.Equal(true, QueryEndpoints.FromJsonElement(JsonSerializer.Deserialize<JsonElement>("true")));
        Assert.Null(QueryEndpoints.FromJsonElement(JsonSerializer.Deserialize<JsonElement>("null")));
    }

    [Fact]
    public void FromJsonElement_ДробноеЧисло_Decimal()
    {
        var el = JsonSerializer.Deserialize<JsonElement>("1.25");

        Assert.Equal(1.25m, QueryEndpoints.FromJsonElement(el));
    }

    // ---------------- ComputeKeyColumns: зеркалит BuildSelectPageSql провайдеров ----------------

    private static TableInfo MakeTable(params string[] pk) => new()
    {
        Schema = "public",
        Name = "t",
        Type = DbObjectType.Table,
        Columns =
        [
            new ColumnInfo { Name = "id", DataType = "int4", OrdinalPosition = 1 },
            new ColumnInfo { Name = "name", DataType = "text", OrdinalPosition = 2 },
        ],
        PrimaryKeyColumns = pk,
    };

    [Fact]
    public void ComputeKeyColumns_ЕстьPk_БезOrderBy_КлючPk()
    {
        var keys = QueryEndpoints.ComputeKeyColumns(
            MakeTable("id"),
            new DataPageRequest { Schema = "public", Table = "t" },
            "ctid");

        Assert.Equal(["id"], keys);
    }

    [Fact]
    public void ComputeKeyColumns_OrderByПлюсPkКакTiebreaker()
    {
        var keys = QueryEndpoints.ComputeKeyColumns(
            MakeTable("id"),
            new DataPageRequest { Schema = "public", Table = "t", OrderBy = "name" },
            "ctid");

        Assert.Equal(["name", "id"], keys);
    }

    [Fact]
    public void ComputeKeyColumns_БезPk_ПсевдоколонкаАдресаСтроки()
    {
        var keys = QueryEndpoints.ComputeKeyColumns(
            MakeTable(),
            new DataPageRequest { Schema = "public", Table = "t" },
            "ROWID");

        Assert.Equal(["ROWID"], keys);
    }

    [Fact]
    public void ComputeKeyColumns_OrderByСовпадаетСPk_БезДублей()
    {
        var keys = QueryEndpoints.ComputeKeyColumns(
            MakeTable("id"),
            new DataPageRequest { Schema = "public", Table = "t", OrderBy = "id" },
            "ctid");

        Assert.Equal(["id"], keys);
    }
}
