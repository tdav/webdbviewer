using WebDbViewer.Web.Security;

namespace WebDbViewer.Tests.Unit.Web;

/// <summary>Тесты хэширования паролей (PBKDF2-SHA256).</summary>
public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_ThenVerify_Succeeds()
    {
        var hash = PasswordHasher.Hash("сложный-пароль-123");
        Assert.True(PasswordHasher.Verify("сложный-пароль-123", hash));
    }

    [Fact]
    public void Verify_WrongPassword_Fails()
    {
        var hash = PasswordHasher.Hash("правильный");
        Assert.False(PasswordHasher.Verify("неправильный", hash));
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        // Соль случайная — хэши не должны совпадать
        Assert.NotEqual(PasswordHasher.Hash("pwd"), PasswordHasher.Hash("pwd"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("мусор")]
    [InlineData("PBKDF2-SHA256$abc$xx$yy")]
    [InlineData("PBKDF2-SHA256$100000$не-base64$тоже")]
    [InlineData("MD5$1$a$b")]
    public void Verify_MalformedStoredHash_Fails(string stored)
    {
        Assert.False(PasswordHasher.Verify("pwd", stored));
    }

    [Fact]
    public void Verify_DefaultAppSettingsHash_MatchesAdmin()
    {
        // Хэш из appsettings.json по умолчанию соответствует паролю "admin"
        const string stored = "PBKDF2-SHA256$100000$+6HCiXh9FzrLu8RTI4bnJQ==$UAa0pVZl4KvK0NkkQ8QXFt6fNIFZh9pPQZdgw3Yo918=";
        Assert.True(PasswordHasher.Verify("admin", stored));
        Assert.False(PasswordHasher.Verify("Admin", stored));
    }
}
