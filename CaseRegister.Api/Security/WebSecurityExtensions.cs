using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CaseRegister.Api.Security;

public static class WebSecurityExtensions
{
    public const string CsrfCookieName = "__Host-csrf";
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    public static IServiceCollection AddWebSecurity(this IServiceCollection services)
    {
        AddSessionCookie(services);
        AddAntiforgery(services);
        AddRateLimiting(services);
        AddForwardedHeaders(services);

        services.AddScoped<SignInAccountRateLimitFilter>();
        services.AddSingleton<SignInAccountLimiter>();

        return services;
    }

    /// <summary>Global MVC filters: validation of every request body, and anti-forgery on every unsafe request.</summary>
    public static void AddWebSecurityFilters(this MvcOptions options)
    {
        options.Filters.Add<ValidationFilter>();

        // Checks the token on POST, PUT, PATCH and DELETE. GET, HEAD, OPTIONS and TRACE are not checked.
        options.Filters.Add<AntiforgeryAuthorizationFilter>();
    }

    private static void AddSessionCookie(IServiceCollection services)
    {
        // Registers the Identity cookie schemes (the sign-in session is the application one).
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(SessionCookie.Configure);

        // Run all cookie and stamp timing from the injected clock, so tests can move time.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<TimeProvider>((options, timeProvider) => options.TimeProvider = timeProvider);

        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            // Default: 30 minutes. A changed password or a suspension ends other sessions within 5.
            options.ValidationInterval = SessionCookie.StampValidationInterval;
        });
        services.AddOptions<SecurityStampValidatorOptions>()
            .Configure<TimeProvider>((options, timeProvider) => options.TimeProvider = timeProvider);
    }

    private static void AddAntiforgery(IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = CsrfCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always; // Default: SameAsRequest.
            options.Cookie.SameSite = SameSiteMode.Strict; // Default: Strict, stated so it cannot change.
            options.Cookie.Path = "/";
            options.HeaderName = CsrfHeaderName; // Default: none (form field only).
        });
    }

    private static void AddRateLimiting(IServiceCollection services)
    {
        services.AddOptions<RateLimitingOptions>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 5 sign-in attempts per minute per client IP, counted whatever the outcome.
            options.AddPolicy(RateLimitPolicies.SignInPerIp, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.SignInPerIpPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(1);
                var result = TooManyRequests.Result(context.HttpContext, retryAfter);

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(result.Value, cancellationToken);
            };
        });
    }

    private static void AddForwardedHeaders(IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // One proxy (Render's) sits in front of the app, so only the last hop is believed. Anything
            // a client adds earlier in X-Forwarded-For is ignored, which stops IP spoofing.
            options.ForwardLimit = 1;

            // The default trusts only loopback. Render's proxy addresses change and the app can only be
            // reached through it, so any sender here is that proxy.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
    }
}
