using Microsoft.AspNetCore.DataProtection;
using WebDbViewer.Web.Security;

namespace WebDbViewer.Tests.Unit.Web;

/// <summary>Тесты ISecretProtector поверх Data Protection API (эфемерные ключи).</summary>
public sealed class DataProtectionSecretProtectorTests
{
    [Fact]
    public void ProtectThenUnprotect_RoundTrips()
    {
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        const string secret = "пароль-к-БД-№1";

        var ciphertext = protector.Protect(secret);

        Assert.NotEqual(secret, ciphertext);
        Assert.Equal(secret, protector.Unprotect(ciphertext));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var protector = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var ciphertext = protector.Protect("секрет");

        var tampered = ciphertext[..^2] + "AA";
        Assert.ThrowsAny<Exception>(() => protector.Unprotect(tampered));
    }
}
