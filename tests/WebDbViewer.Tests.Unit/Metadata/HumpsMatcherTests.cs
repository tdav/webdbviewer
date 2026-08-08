using WebDbViewer.Metadata.Search;

namespace WebDbViewer.Tests.Unit.Metadata;

/// <summary>Тесты camelCase/underscore-матчинга (humps).</summary>
public class HumpsMatcherTests
{
    [Theory]
    // первые буквы «горбов»
    [InlineData("gp", "get_price")]
    [InlineData("gp", "getPrice")]
    [InlineData("gup", "get_unit_price")]
    // подпоследовательности внутри токенов
    [InlineData("usrTbl", "user_table")]
    [InlineData("usrtbl", "user_table")]
    [InlineData("usr", "user_accounts")]
    // точный префикс — тоже валидный humps-случай
    [InlineData("get", "get_price")]
    [InlineData("user_t", "user_table")]
    // регистр не важен
    [InlineData("GP", "get_price")]
    [InlineData("gp", "GET_PRICE")]
    public void IsMatch_Positive(string query, string identifier)
        => Assert.True(HumpsMatcher.IsMatch(query, identifier));

    [Theory]
    [InlineData("xy", "get_price")]
    [InlineData("gpz", "get_price")]
    [InlineData("pg", "get_price")]   // порядок горбов важен
    [InlineData("tblusr", "user_table")]
    [InlineData("", "get_price")]
    [InlineData("gp", "")]
    public void IsMatch_Negative(string query, string identifier)
        => Assert.False(HumpsMatcher.IsMatch(query, identifier));

    [Fact]
    public void Tokenize_SplitsUnderscoresAndCamelCase()
    {
        Assert.Equal(["get", "price"], HumpsMatcher.Tokenize("get_price"));
        Assert.Equal(["get", "price"], HumpsMatcher.Tokenize("getPrice"));
        Assert.Equal(["user", "table", "v", "2"], HumpsMatcher.Tokenize("UserTableV2"));
        Assert.Equal(["order", "items"], HumpsMatcher.Tokenize("ORDER_ITEMS"));
    }
}
