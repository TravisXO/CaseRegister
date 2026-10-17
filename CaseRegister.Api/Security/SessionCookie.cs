using System.Globalization;
using System.Security.Claims;
using CaseRegister.Application.Auth;
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
    public const string SignedInAtKey = AuthenticationClaims.SignedInAtProperty;

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

    // --- The two-factor step cookie ----------------------------------------------------------

    /// <summary>Remembers who passed the password step while they fetch their code. Holds a user id and nothing else.</summary>
    public const string TwoFactorStepName = "__Host-cr-2fa";

    public static readonly TimeSpan TwoFactorStepLifetime = TimeSpan.FromMinutes(5);

    public static void ConfigureTwoFactorStep(CookieAuthenticationOptions options)
    {
        // The same hardening as the session cookie.
        options.Cookie.Name = TwoFactorStepName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // Default: SameAsRequest.
        options.Cookie.SameSite = SameSiteMode.Strict; // Default: Lax.
        options.Cookie.Path = "/";

        // Five minutes from the password step, fixed: using the cookie does not extend it.
        options.ExpireTimeSpan = TwoFactorStepLifetime; // Default: 5 minutes, stated so it cannot drift.
        options.SlidingExpiration = false;

        options.Events = new CookieAuthenticationEvents
        {
            OnSigningIn = context =>
            {
                context.Properties.IsPersistent = false;
                return Task.CompletedTask;
            },
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

    // --- The "remember this device" scheme: present, but switched off ---------------------------

    public const string RememberMeName = "__Host-cr-2fa-remember";

    /// <summary>
    /// Two-factor remember-me is disabled: no code ever issues this cookie, so every sign-in asks for a
    /// code. The scheme still has to be registered, because Identity's security stamp check signs out of
    /// it by name whenever it ends a session, and a missing scheme would turn that into a server error.
    /// So it exists as a handler that honours nothing: should a cookie of this name ever turn up, it is rejected.
    /// </summary>
    public static void ConfigureDisabledRememberMe(CookieAuthenticationOptions options)
    {
        options.Cookie.Name = RememberMeName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.SlidingExpiration = false;

        options.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = context =>
            {
                context.RejectPrincipal();
                return Task.CompletedTask;
            },
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

    /// <summary>
    /// Every few minutes the security stamp check rebuilds the user's claims from the database. The
    /// "how did they sign in" claim (amr) and the session's public id are not in the database, so they
    /// are carried over from the old principal: otherwise a fully signed-in session would silently
    /// drop back to a password-only one.
    /// </summary>
    public static Task CarryClaimsOver(SecurityStampRefreshingPrincipalContext context)
    {
        var identity = context.NewPrincipal?.Identities.FirstOrDefault();
        if (identity is null || context.CurrentPrincipal is null)
        {
            return Task.CompletedTask;
        }

        foreach (var claim in context.CurrentPrincipal.Claims.Where(c => c.Type is AuthenticationClaims.Amr or AuthenticationClaims.SessionId))
        {
            if (!identity.HasClaim(claim.Type, claim.Value))
            {
                identity.AddClaim(new Claim(claim.Type, claim.Value));
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>When the 8-hour limit of this session falls, or null if the ticket does not say when it began.</summary>
    public static DateTimeOffset? EndsAt(AuthenticationProperties? properties) =>
        properties is not null
        && properties.Items.TryGetValue(SignedInAtKey, out var raw)
        && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds) + AbsoluteLimit
            : null;

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
