namespace CaseRegister.Api.Hardening;

/// <summary>
/// The response headers every API response carries. Set when the response starts, so they are on errors,
/// redirects and rate-limit rejections too. Nothing here is a framework default.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment environment)
{
    /// <summary>The policy for the API: no content of any kind may be loaded, and nothing may frame a response.</summary>
    public const string ApiContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";

    /// <summary>
    /// The API reference page in Development only: it loads its own script, styles, fonts and images, and
    /// calls the API on the same origin. It may still not be framed.
    /// </summary>
    public const string ApiReferenceContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob:; font-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; frame-ancestors 'none'";

    public const string ApiReferencePath = "/scalar";

    /// <summary>A year, subdomains included. Not in Development, so localhost is not pinned to https. Set here (not by the HSTS middleware) so an error response still carries it.</summary>
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    /// <summary>Every browser feature switched off.</summary>
    public const string PermissionsPolicy =
        "accelerometer=(), ambient-light-sensor=(), autoplay=(), battery=(), bluetooth=(), camera=(), display-capture=(), "
        + "document-domain=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), hid=(), identity-credentials-get=(), "
        + "idle-detection=(), local-fonts=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), "
        + "publickey-credentials-get=(), screen-wake-lock=(), serial=(), usb=(), web-share=(), xr-spatial-tracking=()";

    public Task InvokeAsync(HttpContext context)
    {
        var isDevelopment = environment.IsDevelopment();
        var useReferencePolicy = isDevelopment && IsApiReference(context.Request.Path);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = useReferencePolicy ? ApiReferenceContentSecurityPolicy : ApiContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers.CacheControl = "no-store";

            // Nothing from another origin may read or embed an API response.
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            // Only over https, as the standard says (behind the proxy, the forwarded scheme decides).
            if (!isDevelopment && context.Request.IsHttps)
            {
                headers.StrictTransportSecurity = StrictTransportSecurity;
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    private static bool IsApiReference(PathString path) => path.StartsWithSegments(ApiReferencePath);
}
