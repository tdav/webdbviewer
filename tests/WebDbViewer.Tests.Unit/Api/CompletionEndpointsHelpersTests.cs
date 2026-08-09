using WebDbViewer.Core;
using WebDbViewer.Web.Api;

namespace WebDbViewer.Tests.Unit.Api;

/// <summary>
/// Схема по умолчанию для автодополнения. До появления этой логики Oracle без явно
/// выбранной схемы не получал метаданных вообще: подсказывались только ключевые слова.
/// </summary>
public class CompletionEndpointsHelpersTests
{
    private static DataSourceConfig Config(DbKind kind, string username) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        Kind = kind,
        Host = "localhost",
        Database = "db",
        Username = username,
    };

    [Fact]
    public void Oracle_БезЯвнойСхемы_БерётсяПользовательПодключения()
    {
        var schema = CompletionEndpoints.DefaultSchemaFor(Config(DbKind.Oracle, "hr"), requested: null);

        // В ALL_* имена хранятся в верхнем регистре.
        Assert.Equal("HR", schema);
    }

    [Fact]
    public void Postgres_БезЯвнойСхемы_БерётсяPublic()
    {
        var schema = CompletionEndpoints.DefaultSchemaFor(Config(DbKind.Postgres, "postgres"), requested: null);

        Assert.Equal("public", schema);
    }

    [Theory]
    [InlineData(DbKind.Oracle)]
    [InlineData(DbKind.Postgres)]
    public void ЯвнаяСхема_ИмеетПриоритетНадУмолчанием(DbKind kind)
    {
        var schema = CompletionEndpoints.DefaultSchemaFor(Config(kind, "hr"), requested: "sales");

        Assert.Equal("sales", schema);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ПустаяСхемаВЗапросе_РавносильнаЕёОтсутствию(string requested)
    {
        var schema = CompletionEndpoints.DefaultSchemaFor(Config(DbKind.Oracle, "hr"), requested);

        Assert.Equal("HR", schema);
    }
}
