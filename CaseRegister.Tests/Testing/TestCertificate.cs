using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CaseRegister.Tests.Testing;

/// <summary>A throwaway self-signed certificate, made once per test run, for the data protection keys.</summary>
public static class TestCertificate
{
    private static readonly Lazy<(string Base64, string Password)> _certificate = new(Create);

    public static string Base64 => _certificate.Value.Base64;

    public static string Password => _certificate.Value.Password;

    /// <summary>Another throwaway certificate, to swap to.</summary>
    public static (string Base64, string Password) CreateAnother(string name = "CaseRegister Tests Next") => Create(name);

    private static (string, string) Create() => Create("CaseRegister Tests");

    private static (string, string) Create(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

        return (Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, password)), password);
    }
}
