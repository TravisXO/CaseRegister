using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace CaseRegister.Infrastructure.KeyProtection;

/// <summary>
/// The certificate that encrypts the data protection keys in the database. It comes from
/// configuration (user-secrets locally, environment variables elsewhere) as a base64 PFX plus its
/// password, and is never committed.
/// </summary>
public static class DataProtectionCertificate
{
    public const string CertificateKey = "DataProtection:CertificateBase64";
    public const string PasswordKey = "DataProtection:CertificatePassword";

    public static X509Certificate2 Load(IConfiguration configuration)
    {
        var base64 = configuration[CertificateKey];
        var password = configuration[PasswordKey];

        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new InvalidOperationException(
                $"{CertificateKey} is not configured, so the data protection keys cannot be encrypted. "
                + "Set it (and " + PasswordKey + ") with 'dotnet user-secrets' locally "
                + "(scripts/New-DevDataProtectionCertificate.ps1 creates one) or as the "
                + "DataProtection__CertificateBase64 and DataProtection__CertificatePassword environment variables.");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64), password);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            // Never include the value or the password in the message.
            throw new InvalidOperationException(
                $"{CertificateKey} could not be read as a base64 PFX certificate, or {PasswordKey} is wrong.",
                ex);
        }
    }
}
