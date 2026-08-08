using WebDbViewer.Providers.Oracle;

namespace WebDbViewer.Tests.Unit.Connections;

/// <summary>Тесты квотирования идентификаторов Oracle (UPPERCASE-правило).</summary>
public class OracleQuotingTests
{
    private readonly OracleProvider _provider = new();

    [Theory]
    [InlineData("USERS")]
    [InlineData("ORDER_ITEMS")]
    [InlineData("T123")]
    [InlineData("EMP$DATA")]
    [InlineData("X#Y")]
    public void SafeUppercaseIdentifiers_NotQuoted(string identifier)
        => Assert.Equal(identifier, _provider.QuoteIdentifier(identifier));

    [Theory]
    [InlineData("users", "\"users\"")]           // нижний регистр — квотируется (иначе Oracle приведёт к UPPER)
    [InlineData("Users", "\"Users\"")]           // смешанный регистр
    [InlineData("MY TABLE", "\"MY TABLE\"")]     // пробел
    [InlineData("1ABC", "\"1ABC\"")]             // начинается с цифры
    [InlineData("_HIDDEN", "\"_HIDDEN\"")]       // в Oracle идентификатор не может начинаться с '_' без кавычек
    public void UnsafeIdentifiers_Quoted(string identifier, string expected)
        => Assert.Equal(expected, _provider.QuoteIdentifier(identifier));

    [Theory]
    [InlineData("SELECT")]
    [InlineData("TABLE")]
    [InlineData("NUMBER")]
    [InlineData("ROWID")]
    public void ReservedWords_Quoted(string identifier)
        => Assert.Equal($"\"{identifier}\"", _provider.QuoteIdentifier(identifier));

    [Fact]
    public void EmbeddedQuotes_Doubled()
        => Assert.Equal("\"A\"\"B\"", _provider.QuoteIdentifier("A\"B"));

    [Fact]
    public void RowAddressPseudoColumn_IsRowid()
        => Assert.Equal("ROWID", _provider.RowAddressPseudoColumn);
}
