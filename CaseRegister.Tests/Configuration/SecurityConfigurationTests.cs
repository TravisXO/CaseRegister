using System.Buffers.Binary;
using CaseRegister.Api.Security;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaseRegister.Tests.Configuration;

public sealed class ConfigurationFixture : IDisposable
{
    public ApiFactory Factory { get; } = ApiFactory.WithoutDatabase();

    public void Dispose() => Factory.Dispose();
}

/// <summary>
/// One test per setting the sign-in design depends on, read from the application's real service
/// container, so a change to any value (or a framework default sneaking back in) fails a test.
/// </summary>
public class SecurityConfigurationTests(ConfigurationFixture fixture) : IClassFixture<ConfigurationFixture>
{
    private IServiceProvider Services => fixture.Factory.Services;

    private T Options<T>()
        where T : class => Services.GetRequiredService<IOptions<T>>().Value;

    // --- Passwords ---------------------------------------------------------------------------

    [Fact]
    public void Passwords_must_be_at_least_12_characters_and_at_most_128()
    {
        Assert.Equal(12, Options<IdentityOptions>().Password.RequiredLength);
        Assert.Equal(12, PasswordPolicy.MinimumLength);
        Assert.Equal(128, PasswordPolicy.MaximumLength);
    }

    [Fact]
    public void No_character_types_are_forced()
    {
        var password = Options<IdentityOptions>().Password;

        Assert.False(password.RequireDigit);
        Assert.False(password.RequireUppercase);
        Assert.False(password.RequireLowercase);
        Assert.False(password.RequireNonAlphanumeric);
    }

    [Fact]
    public void At_least_four_different_characters_are_required()
    {
        Assert.Equal(4, Options<IdentityOptions>().Password.RequiredUniqueChars);
    }

    [Fact]
    public void Password_hashing_uses_210000_iterations_in_the_v3_format()
    {
        var options = Options<PasswordHasherOptions>();
        Assert.Equal(210_000, options.IterationCount);
        Assert.Equal(PasswordHasherCompatibilityMode.IdentityV3, options.CompatibilityMode);

        // The setting really reaches the hash: V3 stores the iteration count in the header.
        using var scope = Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<ApplicationUser>>();
        var bytes = Convert.FromBase64String(hasher.HashPassword(new ApplicationUser(), "a password to hash"));

        Assert.Equal(0x01, bytes[0]); // format marker for V3
        Assert.Equal(210_000u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)));
    }

    [Fact]
    public void Tokens_are_valid_for_15_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Options<DataProtectionTokenProviderOptions>().TokenLifespan);
    }

    // --- Accounts ----------------------------------------------------------------------------

    [Fact]
    public void Email_is_the_username_and_must_be_unique()
    {
        Assert.True(Options<IdentityOptions>().User.RequireUniqueEmail);
    }

    [Fact]
    public void Only_approved_accounts_sign_in_and_there_is_no_email_or_phone_confirmation()
    {
        var signIn = Options<IdentityOptions>().SignIn;

        Assert.True(signIn.RequireConfirmedAccount);
        Assert.False(signIn.RequireConfirmedEmail);
        Assert.False(signIn.RequireConfirmedPhoneNumber);
    }

    [Fact]
    public void The_account_confirmation_is_the_approval_check_not_the_framework_default()
    {
        using var scope = Services.CreateScope();

        Assert.IsType<ApprovedAccountConfirmation>(scope.ServiceProvider.GetRequiredService<IUserConfirmation<ApplicationUser>>());
    }

    [Fact]
    public void Emails_are_looked_up_through_the_one_normaliser()
    {
        Assert.IsType<EmailLookupNormalizer>(Services.GetRequiredService<ILookupNormalizer>());
    }

    // --- Lockout -----------------------------------------------------------------------------

    [Fact]
    public void Five_failed_attempts_lock_an_account_for_15_minutes_and_new_users_can_be_locked()
    {
        var lockout = Options<IdentityOptions>().Lockout;

        Assert.Equal(5, lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), lockout.DefaultLockoutTimeSpan);
        Assert.True(lockout.AllowedForNewUsers);
    }

    [Fact]
    public void Three_lockouts_in_24_hours_suspend_the_account()
    {
        Assert.Equal(3, LockoutTracker.SuspensionThreshold);
        Assert.Equal(TimeSpan.FromHours(24), LockoutTracker.SuspensionWindow);
    }

    // --- Session cookie ----------------------------------------------------------------------

    private CookieAuthenticationOptions SessionCookieOptions() =>
        Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);

    [Fact]
    public void The_session_cookie_is_named_with_the_host_prefix_and_locked_down()
    {
        var cookie = SessionCookieOptions().Cookie;

        Assert.Equal("__Host-cr", cookie.Name);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, cookie.SameSite);
        Assert.Equal("/", cookie.Path);
        Assert.Null(cookie.Domain); // __Host- requires no Domain
    }

    [Fact]
    public void The_session_idles_out_after_30_minutes_with_a_sliding_window_and_8_hours_at_most()
    {
        var options = SessionCookieOptions();

        Assert.Equal(TimeSpan.FromMinutes(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), SessionCookie.AbsoluteLimit);
    }

    [Fact]
    public void The_security_stamp_is_revalidated_every_5_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Options<SecurityStampValidatorOptions>().ValidationInterval);
    }

    [Fact]
    public async Task The_cookie_answers_with_401_and_403_instead_of_redirecting()
    {
        var events = SessionCookieOptions().Events;
        var context = new DefaultHttpContext();
        var options = SessionCookieOptions();

        await events.OnRedirectToLogin(new RedirectContext<CookieAuthenticationOptions>(
            context, new AuthenticationScheme("s", null, typeof(CookieAuthenticationHandler)), options, new AuthenticationProperties(), "/login"));
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey("Location"));

        var denied = new DefaultHttpContext();
        await events.OnRedirectToAccessDenied(new RedirectContext<CookieAuthenticationOptions>(
            denied, new AuthenticationScheme("s", null, typeof(CookieAuthenticationHandler)), options, new AuthenticationProperties(), "/denied"));
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response.StatusCode);
        Assert.False(denied.Response.Headers.ContainsKey("Location"));
    }

    // --- Data protection ---------------------------------------------------------------------

    [Fact]
    public void Data_protection_is_named_CaseRegister_and_keys_last_90_days()
    {
        Assert.Equal("CaseRegister", Options<DataProtectionOptions>().ApplicationDiscriminator);
        Assert.Equal(TimeSpan.FromDays(90), Options<KeyManagementOptions>().NewKeyLifetime);
    }

    [Fact]
    public void Data_protection_keys_are_kept_in_the_database_and_encrypted_with_a_certificate()
    {
        var keys = Options<KeyManagementOptions>();

        Assert.NotNull(keys.XmlRepository);
        Assert.Contains("EntityFrameworkCoreXmlRepository", keys.XmlRepository.GetType().Name, StringComparison.Ordinal);
        Assert.NotNull(keys.XmlEncryptor);
        Assert.Equal("CertificateXmlEncryptor", keys.XmlEncryptor.GetType().Name);
    }

    // --- Anti-forgery ------------------------------------------------------------------------

    [Fact]
    public void The_anti_forgery_cookie_is_host_prefixed_and_locked_down_and_the_header_is_X_CSRF_TOKEN()
    {
        var antiforgery = Options<AntiforgeryOptions>();

        Assert.Equal("__Host-csrf", antiforgery.Cookie.Name);
        Assert.True(antiforgery.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, antiforgery.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, antiforgery.Cookie.SameSite);
        Assert.Equal("X-CSRF-TOKEN", antiforgery.HeaderName);
    }

    [Fact]
    public void Anti_forgery_and_validation_are_global_filters_so_no_endpoint_can_forget_them()
    {
        var filters = Options<MvcOptions>().Filters.OfType<TypeFilterAttribute>().Select(filter => filter.ImplementationType).ToList();

        Assert.Contains(typeof(AntiforgeryAuthorizationFilter), filters);
        Assert.Contains(typeof(ValidationFilter), filters);
    }

    // --- Rate limiting and forwarded headers -------------------------------------------------

    [Fact]
    public void Sign_in_is_limited_to_5_a_minute_per_ip_and_10_an_hour_per_account()
    {
        var defaults = new RateLimitingOptions();

        Assert.Equal(5, defaults.SignInPerIpPerMinute);
        Assert.Equal(10, defaults.SignInPerAccountPerHour);
    }

    [Fact]
    public void Only_one_forwarded_hop_is_believed()
    {
        var forwarded = Options<ForwardedHeadersOptions>();

        Assert.Equal(1, forwarded.ForwardLimit);
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, forwarded.ForwardedHeaders);
    }
}
