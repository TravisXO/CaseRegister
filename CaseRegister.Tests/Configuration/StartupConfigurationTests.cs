using CaseRegister.Tests.Testing;

namespace CaseRegister.Tests.Configuration;

// Not parallel with anything else: one test clears an environment variable.
[Collection(nameof(StartupConfigurationTests))]
public sealed class StartupConfigurationTests : IDisposable
{
    private const string DefaultConnectionVariable = "ConnectionStrings__Default";
    private readonly string? _originalEnvironmentValue = Environment.GetEnvironmentVariable(DefaultConnectionVariable);

    public StartupConfigurationTests()
    {
        // A developer machine may have the real connection string in the environment.
        Environment.SetEnvironmentVariable(DefaultConnectionVariable, null);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(DefaultConnectionVariable, _originalEnvironmentValue);

    [Fact]
    public void The_app_fails_at_startup_when_the_default_connection_string_is_missing()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove("ConnectionStrings:Default"));

        AssertStartupFailsMentioning(factory, "ConnectionStrings:Default");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_app_fails_at_startup_when_the_default_connection_string_is_blank(string value)
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["ConnectionStrings:Default"] = value);

        AssertStartupFailsMentioning(factory, "ConnectionStrings:Default");
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_data_protection_certificate_is_missing()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove("DataProtection:CertificateBase64"));

        AssertStartupFailsMentioning(factory, "DataProtection:CertificateBase64");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_app_fails_at_startup_when_the_data_protection_certificate_is_blank(string value)
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["DataProtection:CertificateBase64"] = value);

        AssertStartupFailsMentioning(factory, "DataProtection:CertificateBase64");
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_certificate_is_not_a_certificate_and_never_echoes_it()
    {
        const string garbage = "bm90LWEtY2VydGlmaWNhdGUtYXQtYWxs";
        using var factory = ApiFactory.WithoutDatabase(settings => settings["DataProtection:CertificateBase64"] = garbage);

        var failure = AssertStartupFailsMentioning(factory, "DataProtection:CertificateBase64");

        Assert.DoesNotContain(garbage, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_certificate_password_is_wrong_and_never_echoes_it()
    {
        const string wrongPassword = "definitely-the-wrong-password";
        using var factory = ApiFactory.WithoutDatabase(settings => settings["DataProtection:CertificatePassword"] = wrongPassword);

        var failure = AssertStartupFailsMentioning(factory, "DataProtection:CertificatePassword");

        Assert.DoesNotContain(wrongPassword, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_turnstile_secret_is_missing()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove("Turnstile:SecretKey"));

        AssertStartupFailsMentioning(factory, "Turnstile:SecretKey");
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_audit_key_is_missing()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove($"Audit:Keys:{ApiFactory.TestKeyId}"));

        AssertStartupFailsMentioning(factory, $"Audit:Keys:{ApiFactory.TestKeyId}");
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_current_audit_key_id_is_missing()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["Audit:CurrentKeyId"] = " ");

        AssertStartupFailsMentioning(factory, "Audit:CurrentKeyId");
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_audit_key_is_too_short_and_never_echoes_it()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);
        using var factory = ApiFactory.WithoutDatabase(settings => settings[$"Audit:Keys:{ApiFactory.TestKeyId}"] = shortKey);

        var failure = AssertStartupFailsMentioning(factory, $"Audit:Keys:{ApiFactory.TestKeyId}");

        Assert.DoesNotContain(shortKey, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_audit_key_is_not_base64()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings[$"Audit:Keys:{ApiFactory.TestKeyId}"] = "not base64 at all!");

        AssertStartupFailsMentioning(factory, $"Audit:Keys:{ApiFactory.TestKeyId}");
    }

    [Fact]
    public void The_app_fails_at_startup_when_the_previous_audit_key_is_named_but_missing()
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings["Audit:PreviousKeyId"] = "k0");

        AssertStartupFailsMentioning(factory, "Audit:Keys:k0");
    }

    // Positive control: proves the failures above are about the missing settings and nothing else.
    [Fact]
    public void The_app_starts_when_everything_required_is_present()
    {
        using var factory = ApiFactory.WithoutDatabase();

        using var client = factory.CreateApiClient().Http;

        Assert.NotNull(client);
    }

    // --- Production: every secret it needs ----------------------------------------------------------------------

    [Theory]
    [InlineData("ConnectionStrings:Default")]
    [InlineData("DataProtection:CertificateBase64")]
    [InlineData("Turnstile:SecretKey")]
    [InlineData("Audit:Keys:k1")]
    [InlineData("Proxy:Secret")]
    [InlineData("Audit:R2:Endpoint")]
    [InlineData("Audit:R2:Bucket")]
    [InlineData("Audit:R2:AccessKeyId")]
    [InlineData("Audit:R2:SecretAccessKey")]
    [InlineData("Invites:SetupPageUrl")]
    [InlineData("Deployment:Mode")]
    public void Production_will_not_start_without_each_setting_it_needs_and_names_the_setting_never_a_value(string setting)
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings.Remove(setting), environmentName: "Production");

        var failure = AssertStartupFailsMentioning(factory, setting);

        // None of the other secrets is in the message either.
        foreach (var value in new[] { ApiFactory.ProxyTestSecret, ApiFactory.TestKeyBase64, ApiFactory.TurnstileTestSecret, "secret-access-key-for-tests", "access-key-for-tests" })
        {
            Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Production_starts_when_everything_it_needs_is_present()
    {
        using var factory = ApiFactory.WithoutDatabase(environmentName: "Production");

        using var client = factory.CreateApiClient().Http;

        Assert.NotNull(client);
    }

    private static Exception AssertStartupFailsMentioning(ApiFactory factory, string setting)
    {
        // The Testing environment does not load user-secrets, so nothing else supplies a value.
        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains(setting, failure.ToString(), StringComparison.Ordinal);

        return failure;
    }
}
