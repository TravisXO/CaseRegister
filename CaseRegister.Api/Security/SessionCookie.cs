using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Api.Security;

/// <summary>
/// The sign-in session cookie. Every value is set here on purpose rather than taken from the
/// framework default, and a comment says where it differs from that default.
/// </summary>
public static class SessionCookie
{
    // The __Host- prefix makes browsers insist on Secure, Path=/ and no Domain, so the cookie
    // cannot be set by a sibling subdomain or over plain HTTP.
    public const string Name = "__Host-cr";

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLimit = TimeSpan.FromHours(8);
    public static readonly TimeSpan StampValidationInterval = TimeSpan.FromMinutes(5);

    /// <summary>When the user actually signed in. Kept in the cookie ticket so renewals cannot reset it.</summary>
    public const string SignedInAtKey = "cr.signed_in_at";

    public static void Configure(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = Name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // Default: SameAsRequest.
        options.Cookie.SameSite = SameSiteMode.Strict; // Default: Lax.
        options.Cookie.Path = "/";

        // Idle timeout: the session ends after 30 idle minutes, and activity pushes that back.
        options.ExpireTimeSpan = IdleTimeout; // Default: 14 days.
        options.SlidingExpiration = true;

        options.Events = new CookieAuthenticationEvents
        {
            OnSigningIn = context =>
            {
                // Always a session cookie, never "remember me", whatever the caller asked for.
                context.Properties.IsPersistent = false;

                if (!context.Properties.Items.ContainsKey(SignedInAtKey))
                {
                    var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                    context.Properties.Items[SignedInAtKey] = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                }

                return Task.CompletedTask;
            },

            OnValidatePrincipal = async context =>
            {
                if (AbsoluteLimitReached(context))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return;
                }

                // Also re-checks the security stamp every StampValidationInterval.
                await SecurityStampValidator.ValidatePrincipalAsync(context);
            },

            // An API answers with a status code. It never redirects to a login page.
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            },
        };
    }

    // However active the user has been, nobody stays signed in for more than the absolute limit.
    // A ticket with no sign-in time is refused.
    private static bool AbsoluteLimitReached(CookieValidatePrincipalContext context)
    {
        if (!context.Properties.Items.TryGetValue(SignedInAtKey, out var raw)
            || !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return true;
        }

        var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();

        return now - DateTimeOffset.FromUnixTimeSeconds(seconds) >= AbsoluteLimit;
    }
}
