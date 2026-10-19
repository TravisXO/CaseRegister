using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;

namespace CaseRegister.Infrastructure.KeyProtection;

/// <summary>
/// The certificates that protect the data protection keys in the database. The current one protects new keys. Older ones are kept only for
/// decrypting keys that were protected before the certificate was replaced: without them those keys, and everything encrypted with them
/// (sessions, authenticator keys), could never be read again. Every certificate is held for as long as the application runs.
///
/// Each comes from configuration (user-secrets locally, environment variables or secret files elsewhere), never from code:
///  - the current one:  DataProtection:CertificatePath (a .pfx file) or DataProtection:CertificateBase64, and DataProtection:CertificatePassword;
///  - older ones:       DataProtection:Previous:0:CertificatePath (or :CertificateBase64) and :CertificatePassword, then :1:, and so on.
/// Loading is done with <see cref="X509CertificateLoader"/>; no obsolete constructor and no thumbprint lookup in a machine store.
/// </summary>
public sealed class DataProtectionCertificates : IDisposable
{
    public const string CertificateKey = "DataProtection:CertificateBase64";
    public const string PathKey = "DataProtection:CertificatePath";
    public const string PasswordKey = "DataProtection:CertificatePassword";
    public const string PreviousSection = "DataProtection:Previous";

    private DataProtectionCertificates(X509Certificate2 current, IReadOnlyList<X509Certificate2> previous)
    {
        Current = current;
        Previous = previous;
        All = [current, .. previous];
    }

    public X509Certificate2 Current { get; }

    /// <summary>Older certificates, for decrypting only.</summary>
    public IReadOnlyList<X509Certificate2> Previous { get; }

    public IReadOnlyList<X509Certificate2> All { get; }

    /// <summary>Loads them all. Throws, at startup, with a message that names the setting and never shows a value, if the current one is missing or any cannot be read.</summary>
    public static DataProtectionCertificates Load(IConfiguration configuration)
    {
        var current = LoadOne(configuration, string.Empty, required: true, CertificateKey, PathKey, PasswordKey)!;
        List<X509Certificate2> previous = [];

        try
        {
            foreach (var entry in configuration.GetSection(PreviousSection).GetChildren())
            {
                var prefix = $"{PreviousSection}:{entry.Key}:";
                previous.Add(LoadOne(entry, prefix, required: true, prefix + "CertificateBase64", prefix + "CertificatePath", prefix + "CertificatePassword")!);
            }
        }
        catch
        {
            current.Dispose();
            foreach (var certificate in previous)
            {
                certificate.Dispose();
            }

            throw;
        }

        return new DataProtectionCertificates(current, previous);
    }

    private static X509Certificate2? LoadOne(IConfiguration source, string keyPrefix, bool required, string base64Name, string pathName, string passwordName)
    {
        // For the current certificate the settings are read from the root by their full names; for an older one from its own section by their short names.
        string? Read(string fullName) => keyPrefix.Length == 0 ? source[fullName] : source[fullName[keyPrefix.Length..]];

        var path = Read(pathName);
        var base64 = Read(base64Name);
        var password = Read(passwordName);

        if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(base64))
        {
            return required
                ? throw new InvalidOperationException(
                    $"{base64Name} is not configured (or {pathName}), so the data protection keys cannot be encrypted. "
                    + $"Set it (and {passwordName}) with 'dotnet user-secrets' locally "
                    + "(scripts/New-DevDataProtectionCertificate.ps1 creates one) or as the "
                    + "DataProtection__CertificateBase64 and DataProtection__CertificatePassword environment variables. "
                    + "The application will not start without it: starting with a different certificate would leave the stored keys unreadable.")
                : null;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(path)
                ? X509CertificateLoader.LoadPkcs12FromFile(path, password)
                : X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64!), password);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Never include the value, the path's contents or the password in the message.
            throw new InvalidOperationException(
                $"{(string.IsNullOrWhiteSpace(path) ? base64Name : pathName)} could not be read as a PFX certificate, or {passwordName} is wrong.",
                ex);
        }
    }

    public void Dispose()
    {
        foreach (var certificate in All)
        {
            certificate.Dispose();
        }
    }
}
