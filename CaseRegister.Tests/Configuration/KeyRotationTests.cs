using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.KeyProtection;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Configuration;

/// <summary>Loading the data protection certificates. No database: these run anywhere, including in the Linux image.</summary>
public sealed class DataProtectionCertificateLoadingTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(pair => pair.Key, pair => pair.Value)).Build();

    private static string FileOf(string base64)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cr-cert-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, Convert.FromBase64String(base64));

        return path;
    }

    [Fact]
    public void The_current_certificate_loads_from_base64_or_from_a_file_and_can_do_private_key_work()
    {
        var path = FileOf(TestCertificate.Base64);
        try
        {
            using var fromBase64 = DataProtectionCertificates.Load(Config((DataProtectionCertificates.CertificateKey, TestCertificate.Base64), (DataProtectionCertificates.PasswordKey, TestCertificate.Password)));
            using var fromFile = DataProtectionCertificates.Load(Config((DataProtectionCertificates.PathKey, path), (DataProtectionCertificates.PasswordKey, TestCertificate.Password)));

            Assert.Equal(fromBase64.Current.Thumbprint, fromFile.Current.Thumbprint);
            Assert.True(fromFile.Current.HasPrivateKey);

            // The private key really works (decrypting is what the key ring does with it), on this operating system.
            using var rsa = fromFile.Current.GetRSAPrivateKey()!;
            var sealedBytes = fromFile.Current.GetRSAPublicKey()!.Encrypt([1, 2, 3, 4], RSAEncryptionPadding.OaepSHA1);
            Assert.Equal<byte>([1, 2, 3, 4], rsa.Decrypt(sealedBytes, RSAEncryptionPadding.OaepSHA1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Older_certificates_are_loaded_after_the_current_one_and_kept()
    {
        var (olderBase64, olderPassword) = TestCertificate.CreateAnother("Older");
        var (oldestBase64, oldestPassword) = TestCertificate.CreateAnother("Oldest");

        using var all = DataProtectionCertificates.Load(Config(
            (DataProtectionCertificates.CertificateKey, TestCertificate.Base64),
            (DataProtectionCertificates.PasswordKey, TestCertificate.Password),
            ($"{DataProtectionCertificates.PreviousSection}:0:CertificateBase64", olderBase64),
            ($"{DataProtectionCertificates.PreviousSection}:0:CertificatePassword", olderPassword),
            ($"{DataProtectionCertificates.PreviousSection}:1:CertificateBase64", oldestBase64),
            ($"{DataProtectionCertificates.PreviousSection}:1:CertificatePassword", oldestPassword)));

        Assert.Equal(2, all.Previous.Count);
        Assert.Equal(3, all.All.Count);
        Assert.Equal(3, all.All.Select(certificate => certificate.Thumbprint).Distinct().Count());
        Assert.All(all.All, certificate => Assert.True(certificate.HasPrivateKey));
    }

    [Fact]
    public void A_missing_certificate_is_a_clear_error_that_names_the_setting_and_says_why_it_matters()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificates.Load(Config()));

        Assert.Contains("DataProtection:CertificateBase64", failure.Message, StringComparison.Ordinal);
        Assert.Contains("will not start", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrong_password_a_bad_file_or_a_missing_file_never_shows_a_secret()
    {
        var secret = "hunter2-the-password";
        var path = FileOf(TestCertificate.Base64);
        try
        {
            var wrongPassword = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificates.Load(Config((DataProtectionCertificates.PathKey, path), (DataProtectionCertificates.PasswordKey, secret))));
            var missingFile = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificates.Load(Config((DataProtectionCertificates.PathKey, path + ".gone"), (DataProtectionCertificates.PasswordKey, secret))));
            var older = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificates.Load(Config(
                (DataProtectionCertificates.CertificateKey, TestCertificate.Base64),
                (DataProtectionCertificates.PasswordKey, TestCertificate.Password),
                ($"{DataProtectionCertificates.PreviousSection}:0:CertificateBase64", "###not base64###"),
                ($"{DataProtectionCertificates.PreviousSection}:0:CertificatePassword", secret))));

            Assert.Contains("DataProtection:CertificatePath", wrongPassword.Message, StringComparison.Ordinal);
            Assert.Contains("DataProtection:CertificatePath", missingFile.Message, StringComparison.Ordinal);
            Assert.Contains("DataProtection:Previous:0:CertificateBase64", older.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, wrongPassword.ToString() + missingFile + older, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void No_code_uses_the_obsolete_certificate_constructors_or_a_thumbprint_lookup()
    {
        var source = File.ReadAllText(Path.Combine(Architecture.SolutionGraph.SolutionRoot, "CaseRegister.Infrastructure", "KeyProtection", "DataProtectionCertificate.cs"));

        Assert.Contains("X509CertificateLoader.LoadPkcs12FromFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new X509Certificate2(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("X509Store", source, StringComparison.Ordinal);
    }

    private static readonly string[] _projects = ["CaseRegister.Api", "CaseRegister.Infrastructure", "CaseRegister.Application"];

    [Fact]
    public void Nothing_in_the_application_revokes_or_deletes_data_protection_keys()
    {
        var root = Architecture.SolutionGraph.SolutionRoot;
        var sources = _projects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !file.Contains("Migrations", StringComparison.Ordinal));

        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("RevokeKey", text, StringComparison.Ordinal);
            Assert.DoesNotContain("RevokeAllKeys", text, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"DataProtectionKeys\s*\.\s*(Remove|ExecuteDelete)", text);
        }
    }
}

/// <summary>A certificate swap against the real key table: what already exists keeps working, and a swap without the old certificate refuses to start.</summary>
[Collection(PostgresTestGroup.Name)]
public sealed class CertificateSwapTests(PostgresFixture postgres)
{
    private static (string Base64, string Password) _next = TestCertificate.CreateAnother();

    private static void SwapTo(Dictionary<string, string?> settings, bool keepOld)
    {
        settings["DataProtection:CertificateBase64"] = _next.Base64;
        settings["DataProtection:CertificatePassword"] = _next.Password;
        if (keepOld)
        {
            settings["DataProtection:Previous:0:CertificateBase64"] = TestCertificate.Base64;
            settings["DataProtection:Previous:0:CertificatePassword"] = TestCertificate.Password;
        }
    }

    [Fact]
    public async Task After_a_certificate_swap_existing_totp_sign_in_still_works_and_new_keys_use_the_new_certificate()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync("swap@example.test"); // its authenticator key is protected with the first certificate
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user)).StatusCode);
        var rowsBefore = await factory.WithDbAsync(db => db.DataProtectionKeys.CountAsync());
        Assert.True(rowsBefore > 0);

        // A new instance of the application on the same database, with the second certificate current and the first kept for reading.
        await using var swapped = factory.CreateSibling(settings => SwapTo(settings, keepOld: true));
        swapped.RememberTwoFactorKey(user, factory.TwoFactorKey(user));
        swapped.Clock.Advance(TimeSpan.FromMinutes(5)); // the first sign-in used this code step; a code works once

        var signIn = await swapped.CreateApiClient().SignInWithTwoFactorAsync(swapped, user);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        // The old keys were read with the old certificate; a key made now is protected with the new one only.
        using var scope = swapped.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IKeyManager>();
        manager.CreateNewKey(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(91));
        var newest = await swapped.WithDbAsync(db => db.DataProtectionKeys.AsNoTracking().OrderByDescending(row => row.Id).Select(row => row.Xml).FirstAsync());
        using var current = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(_next.Base64), _next.Password);
        using var old = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(TestCertificate.Base64), TestCertificate.Password);
        Assert.Contains(Convert.ToBase64String(current.RawData), newest, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(old.RawData), newest, StringComparison.Ordinal);
        Assert.True(await swapped.WithDbAsync(db => db.DataProtectionKeys.CountAsync()) > rowsBefore); // added to, never replaced

        // And the first instance, still on the old certificate alone, is not able to read the key the new one made (it is not asked to).
        factory.Clock.Advance(TimeSpan.FromMinutes(10)); // past the code step the other instance used
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user)).StatusCode);
    }

    [Fact]
    public async Task A_swap_without_the_old_certificate_stops_the_application_starting_and_creates_no_new_key()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync("lost@example.test");
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user)).StatusCode);
        var rowsBefore = await factory.WithDbAsync(db => db.DataProtectionKeys.CountAsync());

        await using var swapped = factory.CreateSibling(settings => SwapTo(settings, keepOld: false));
        var failure = Record.Exception(() => swapped.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains("data protection key", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("DataProtection:Previous", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(rowsBefore, await factory.WithDbAsync(db => db.DataProtectionKeys.CountAsync())); // nothing was created behind anyone's back
    }

    [Fact]
    public async Task The_application_role_cannot_delete_or_change_a_data_protection_key()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.CreateUserAsync("keys@example.test");

        var failure = await Record.ExceptionAsync(() => factory.WithDbAsync(async db => await db.Database.ExecuteSqlRawAsync("DELETE FROM data_protection_keys")));
        var update = await Record.ExceptionAsync(() => factory.WithDbAsync(async db => await db.Database.ExecuteSqlRawAsync("UPDATE data_protection_keys SET xml = xml")));

        Assert.Contains("permission denied", failure?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permission denied", update?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(await factory.WithDbAsync(db => db.DataProtectionKeys.CountAsync()) > 0);
    }

    [Fact]
    public async Task Verify_keys_passes_on_a_healthy_deployment_and_fails_when_a_certificate_is_missing()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        var user = await factory.CreateUserAsync("verify@example.test");
        await factory.CreateApiClient().SignInWithTwoFactorAsync(factory, user);
        await factory.WriteManyAsync(2);

        using (var scope = factory.Services.CreateScope())
        {
            var checks = await scope.ServiceProvider.GetRequiredService<KeyVerifier>().VerifyAsync();

            Assert.All(checks, check => Assert.True(check.Passed, $"{check.Name}: {check.Detail}"));
            Assert.Contains(checks, check => check.Name == "Authenticator key sample" && check.Detail.Contains("decrypts", StringComparison.Ordinal));
            Assert.Contains(checks, check => check.Name == "Audit chain" && check.Detail.Contains("entries checked", StringComparison.Ordinal));
        }

        // Nothing in any check's text is a key or a password.
        using var again = factory.Services.CreateScope();
        var text = string.Join('\n', (await again.ServiceProvider.GetRequiredService<KeyVerifier>().VerifyAsync()).Select(check => check.Detail));
        Assert.DoesNotContain(ApiFactory.TestKeyBase64, text, StringComparison.Ordinal);
        Assert.DoesNotContain(TestCertificate.Password, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_keys_fails_when_the_audit_chain_has_lost_a_key()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        await factory.WriteManyAsync(2);

        await using var forgotten = factory.CreateSibling(settings =>
        {
            settings["Audit:CurrentKeyId"] = ApiFactory.SecondKeyId;
            settings.Remove($"Audit:Keys:{ApiFactory.TestKeyId}");
            settings[$"Audit:Keys:{ApiFactory.SecondKeyId}"] = ApiFactory.SecondKeyBase64;
        });
        using var scope = forgotten.Services.CreateScope();

        var checks = await scope.ServiceProvider.GetRequiredService<KeyVerifier>().VerifyAsync();

        var chain = Assert.Single(checks, check => check.Name == "Audit chain");
        Assert.False(chain.Passed);
        Assert.Contains("UnknownKey", chain.Detail, StringComparison.Ordinal);
    }
}
