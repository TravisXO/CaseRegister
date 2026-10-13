using CaseRegister.Api.Hardening;
using CaseRegister.Tests.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Configuration;

/// <summary>Every options class is checked when the application starts. A bad setting stops it, with a message that names the setting and never shows its value.</summary>
public sealed class OptionsValidationTests
{
    // Every options class in the solution. A new one has to be added here, which means it has to be validated.
    private static readonly string[] _expectedOptionsClasses =
    [
        "AuditOptions", "DatabaseOptions", "ExternalCallOptions", "MembershipOptions", "RateLimitingOptions", "StaffInviteOptions", "TurnstileOptions", "TwoFactorOptions",
    ];

    [Fact]
    public void Every_options_class_in_the_solution_is_accounted_for_and_has_a_validator_registered()
    {
        var found = new[] { typeof(Program).Assembly, typeof(CaseRegister.Infrastructure.DependencyInjection).Assembly, typeof(CaseRegister.Application.Auditing.AuditOptions).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false, IsSealed: true } && type.Name.EndsWith("Options", StringComparison.Ordinal) && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToList();
        Assert.Equal(_expectedOptionsClasses, found.Select(type => type.Name).Order());

        using var factory = ApiFactory.WithoutDatabase();
        foreach (var options in found)
        {
            var validators = factory.Services.GetServices(typeof(IValidateOptions<>).MakeGenericType(options));

            Assert.True(validators.Any(), $"{options.Name} has no validation");
        }
    }

    // A bad value, the setting that has to be named, and a piece of the value that must not appear in the failure.
    [Theory]
    [InlineData("ConnectionStrings:Default", " ", "ConnectionStrings:Default", null)]
    [InlineData("Turnstile:SecretKey", " ", "Turnstile:SecretKey", null)]
    [InlineData("Audit:CurrentKeyId", " ", "Audit:CurrentKeyId", null)]
    [InlineData("Audit:Keys:k1", "dG9vLXNob3J0LXNlY3JldC1rZXk=", "Audit:Keys:k1", "dG9vLXNob3J0")]
    [InlineData("Membership:DisplayPrefix", "9secret9", "Membership:DisplayPrefix", "9secret9")]
    [InlineData("Invites:SetupPageUrl", "ftp://secret-host-4412.example/setup", "Invites:SetupPageUrl", "secret-host-4412")]
    [InlineData("RateLimiting:GlobalPerIpPerMinute", "0", "RateLimiting:GlobalPerIpPerMinute", null)]
    [InlineData("RateLimiting:SignInPerIpPerMinute", "-3", "RateLimiting:SignInPerIpPerMinute", null)]
    [InlineData("RateLimiting:SignInPerAccountPerHour", "0", "RateLimiting:SignInPerAccountPerHour", null)]
    [InlineData("RateLimiting:SignUpPerIpPerHour", "0", "RateLimiting:SignUpPerIpPerHour", null)]
    [InlineData("RateLimiting:StaffSetupPerIpPerMinute", "0", "RateLimiting:StaffSetupPerIpPerMinute", null)]
    [InlineData("TwoFactor:AttemptsPerMinutePerUser", "0", "TwoFactor:AttemptsPerMinutePerUser", null)]
    [InlineData("ExternalCalls:MaxRetryAttempts", "99", "ExternalCalls:MaxRetryAttempts", null)]
    [InlineData("ExternalCalls:AttemptTimeout", "00:00:00", "ExternalCalls:AttemptTimeout", null)]
    [InlineData("ExternalCalls:TotalTimeout", "00:00:01", "ExternalCalls:TotalTimeout", null)]
    [InlineData("ExternalCalls:RetryDelay", "00:00:00", "ExternalCalls:RetryDelay", null)]
    public void A_bad_setting_stops_the_application_at_startup_naming_the_setting_and_not_its_value(string key, string badValue, string expectedName, string? valueThatMustNotAppear)
    {
        using var factory = ApiFactory.WithoutDatabase(settings => settings[key] = badValue, realLimits: true);

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains(expectedName, failure.ToString(), StringComparison.Ordinal);
        if (valueThatMustNotAppear is not null)
        {
            Assert.DoesNotContain(valueThatMustNotAppear, failure.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Startup_reports_every_problem_in_a_section_not_just_the_first()
    {
        using var factory = ApiFactory.WithoutDatabase(
            settings =>
            {
                settings["RateLimiting:GlobalPerIpPerMinute"] = "0";
                settings["RateLimiting:SignUpPerIpPerHour"] = "0";
            },
            realLimits: true);

        var failure = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(failure);
        Assert.Contains("RateLimiting:GlobalPerIpPerMinute", failure.ToString(), StringComparison.Ordinal);
        Assert.Contains("RateLimiting:SignUpPerIpPerHour", failure.ToString(), StringComparison.Ordinal);
    }

    // --- The port ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void With_no_port_given_the_image_default_applies(string? port)
    {
        Assert.Null(HardeningExtensions.ListenUrlFor(port));
    }

    [Theory]
    [InlineData("10000", "http://0.0.0.0:10000")]
    [InlineData("8080", "http://0.0.0.0:8080")]
    [InlineData("1", "http://0.0.0.0:1")]
    [InlineData("65535", "http://0.0.0.0:65535")]
    public void The_port_the_platform_gives_is_the_one_listened_on_over_plain_http(string port, string expected)
    {
        Assert.Equal(expected, HardeningExtensions.ListenUrlFor(port));
    }

    [Theory]
    [InlineData("not-a-port-77")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-5")]
    [InlineData("80 80")]
    [InlineData("8080;http://evil")]
    [InlineData("1e3")]
    public void A_bad_port_stops_startup_naming_the_variable_and_not_what_it_held(string port)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => HardeningExtensions.ListenUrlFor(port));

        Assert.Contains("PORT", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(port, failure.Message, StringComparison.Ordinal);
    }
}
