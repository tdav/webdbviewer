using WebDbViewer.Providers.Postgres;

namespace WebDbViewer.Tests.Unit.Connections;

/// <summary>Тесты квотирования идентификаторов PostgreSQL (lowercase-правило).</summary>
public class PostgresQuotingTests
{
    private readonly PostgresProvider _provider = new();

    [Theory]
    [InlineData("users")]
    [InlineData("_internal")]
    [InlineData("order_items")]
    [InlineData("t123")]
    public void SafeLowercaseIdentifiers_NotQuoted(string identifier)
        => Assert.Equal(identifier, _provider.QuoteIdentifier(identifier));

    [Theory]
    [InlineData("Users", "\"Users\"")]           // верхний регистр — квотируется
    [InlineData("ORDER_ITEMS", "\"ORDER_ITEMS\"")]
    [InlineData("my table", "\"my table\"")]     // пробел
    [InlineData("1abc", "\"1abc\"")]             // начинается с цифры
    [InlineData("колонка", "\"колонка\"")]       // не-ASCII
    [InlineData("a-b", "\"a-b\"")]
    public void UnsafeIdentifiers_Quoted(string identifier, string expected)
        => Assert.Equal(expected, _provider.QuoteIdentifier(identifier));

    [Theory]
    [InlineData("select")]
    [InlineData("table")]
    [InlineData("where")]
    [InlineData("user")]
    public void ReservedWords_Quoted(string identifier)
        => Assert.Equal($"\"{identifier}\"", _provider.QuoteIdentifier(identifier));

    [Fact]
    public void EmbeddedQuotes_Doubled()
        => Assert.Equal("\"o\"\"reilly\"", _provider.QuoteIdentifier("o\"reilly"));

    [Fact]
    public void EmptyIdentifier_Throws()
        => Assert.Throws<ArgumentException>(() => _provider.QuoteIdentifier(""));

    [Fact]
    public void RowAddressPseudoColumn_IsCtid()
        => Assert.Equal("ctid", _provider.RowAddressPseudoColumn);
}
