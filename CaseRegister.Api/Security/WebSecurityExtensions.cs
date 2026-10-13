using System.Threading.RateLimiting;
using CaseRegister.Api.Hardening;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
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

        // A body must be JSON, and every problem an action returns carries the trace id.
        options.Filters.Add<JsonBodyOnlyFilter>();
        options.Filters.Add<ProblemDetailsTraceFilter>();

        // Checks the token on POST, PUT, PATCH and DELETE. GET, HEAD, OPTIONS and TRACE are not checked.
        options.Filters.Add<AntiforgeryAuthorizationFilter>();
    }

    private static void AddSessionCookie(IServiceCollection services)
    {
        // Identity's AddIdentityCookies would register four schemes. Only what is needed is registered:
        //   - the session, and the short two-factor step (both hardened);
        //   - "remember this device", present only because the security stamp check signs out of it by
        //     name; it is inert and honours nothing (two-factor remember-me is disabled);
        //   - no external-login scheme at all (there are no external logins).
        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, SessionCookie.Configure)
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme, SessionCookie.ConfigureTwoFactorStep)
            .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, SessionCookie.ConfigureDisabledRememberMe);

        // Sessions are kept on the server, so the session cookie holds only a session id. All cookie and
        // stamp timing runs from the injected clock, so tests can move time.
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<TimeProvider, ITicketStore>((options, timeProvider, store) =>
            {
                options.TimeProvider = timeProvider;
                options.SessionStore = store;
            });
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme)
            .Configure<TimeProvider>((options, timeProvider) => options.TimeProvider = timeProvider);

        services.Configure<SecurityStampValidatorOptions>(options =>
        {
            // Default: 30 minutes. A changed password or a suspension ends other sessions within 5.
            options.ValidationInterval = SessionCookie.StampValidationInterval;
            options.OnRefreshingPrincipal = SessionCookie.CarryClaimsOver;
        });
        services.AddOptions<SecurityStampValidatorOptions>()
            .Configure<TimeProvider>((options, timeProvider) => options.TimeProvider = timeProvider);

        services.AddAuthorization(AuthorizationPolicies.Configure);
        services.AddAuthorizationHandlers();
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
        services.AddOptions<RateLimitingOptions>()
            .BindConfiguration(RateLimitingOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitingOptions>, RateLimitingOptionsValidator>();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // A limit for every endpoint, on top of the named ones below. The health checks opt out of it (DisableRateLimiting).
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.GlobalPerIpPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

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

            // 3 sign-ups per hour per client IP, counted whatever the outcome (so it says nothing about any account).
            options.AddPolicy(RateLimitPolicies.SignUpPerIp, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.SignUpPerIpPerHour,
                    Window = TimeSpan.FromHours(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
            });

            // The staff setup steps: a few a minute per client IP, on top of the count of failed tokens.
            options.AddPolicy(RateLimitPolicies.StaffSetupPerIp, httpContext =>
            {
                var limits = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = limits.StaffSetupPerIpPerMinute,
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
                ProblemResponses.AddTraceId((ProblemDetails)result.Value!, context.HttpContext);

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync((ProblemDetails)result.Value!, options: null, contentType: "application/problem+json", cancellationToken);
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
