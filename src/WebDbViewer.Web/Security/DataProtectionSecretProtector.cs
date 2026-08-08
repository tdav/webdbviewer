using Microsoft.AspNetCore.DataProtection;
using WebDbViewer.Core;

namespace WebDbViewer.Web.Security;

/// <summary>
/// Реализация <see cref="ISecretProtector"/> поверх ASP.NET Core Data Protection API.
/// Используется для шифрования паролей датасорсов перед сохранением в метабазу.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    /// <summary>Назначение (purpose) ключа — менять нельзя, иначе старые секреты не расшифруются.</summary>
    private const string Purpose = "WebDbViewer.DataSourceSecrets.v1";

    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return _protector.Protect(plaintext);
    }

    /// <inheritdoc />
    public string Unprotect(string ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return _protector.Unprotect(ciphertext);
    }
}
