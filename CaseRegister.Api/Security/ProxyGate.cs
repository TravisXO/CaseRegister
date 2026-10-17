using System.Net;
using System.Security.Cryptography;
using System.Text;
using CaseRegister.Api.Hardening;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CaseRegister.Api.Security;

/// <summary>
/// The shared secret the Cloudflare Pages Function adds to every request it sends on, so the API answers nobody else. Two are
/// accepted at once (the current one and the next one), which is what makes a rotation possible with no downtime: add the next
/// secret here, switch the proxy to it, then make it the current one. Values come from the environment (Proxy__Secret and
/// Proxy__NextSecret), never from code, and no message ever shows one.
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";
    public const string SecretKey = "Proxy:Secret";
    public const string NextSecretKey = "Proxy:NextSecret";
    public const int MinimumSecretLength = 32;

    /// <summary>The header the proxy puts the secret in.</summary>
    public const string SecretHeaderName = "X-Proxy-Secret";

    /// <summary>
    /// The header the proxy puts the visitor's address in. The Pages Function copies it from CF-Connecting-IP, which only Cloudflare
    /// sets, into a header of our own name: Cloudflare may rewrite its own CF- headers on a request the function sends on.
    /// It is believed only on a request that has passed the secret check.
    /// </summary>
    public const string ClientIpHeaderName = "X-Client-Ip";

    public string? Secret { get; set; }

    public string? NextSecret { get; set; }

    /// <summary>True when a secret is configured, which is when every request but the liveness check must carry one.</summary>
    public bool Enabled => !string.IsNullOrEmpty(Secret);
}

/// <summary>Stops startup, naming the setting and never its value, if the proxy secrets are missing (outside Development and Testing) or too weak.</summary>
public sealed class ProxyOptionsValidator(IHostEnvironment environment) : IValidateOptions<ProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ProxyOptions options)
    {
        if (string.IsNullOrEmpty(options.Secret))
        {
            if (!string.IsNullOrEmpty(options.NextSecret))
            {
                return ValidateOptionsResult.Fail($"{ProxyOptions.NextSecretKey} is set but {ProxyOptions.SecretKey} is not. Keep the current secret in {ProxyOptions.SecretKey} while the next one is introduced.");
            }

            return environment.IsProduction()
                ? ValidateOptionsResult.Fail($"{ProxyOptions.SecretKey} is not configured, so the API would answer anyone who reaches it. Set the Proxy__Secret environment variable (at least {ProxyOptions.MinimumSecretLength} characters).")
                : ValidateOptionsResult.Success;
        }

        foreach (var (key, value) in new[] { (ProxyOptions.SecretKey, options.Secret), (ProxyOptions.NextSecretKey, options.NextSecret) })
        {
            if (value is not null && (value.Length < ProxyOptions.MinimumSecretLength || string.IsNullOrWhiteSpace(value)))
            {
                return ValidateOptionsResult.Fail($"{key} must be at least {ProxyOptions.MinimumSecretLength} characters.");
            }
        }

        return string.Equals(options.Secret, options.NextSecret, StringComparison.Ordinal)
            ? ValidateOptionsResult.Fail($"{ProxyOptions.NextSecretKey} must differ from {ProxyOptions.SecretKey}.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Refuses every request that does not carry the proxy secret, except the platform's liveness check. The comparison takes the
/// same time whatever the header holds (both secrets are always compared, over fixed-length hashes). A request that passes
/// also tells us the visitor's address; that header is read here and nowhere else, and only after the secret has been accepted,
/// so a visitor cannot choose the address the rate limits and the audit log see.
/// </summary>
public sealed partial class ProxyGateMiddleware : IMiddleware
{
    private readonly byte[]? _current;
    private readonly byte[] _next;
    private readonly ILogger<ProxyGateMiddleware> _logger;

    public ProxyGateMiddleware(IOptions<ProxyOptions> options, ILogger<ProxyGateMiddleware> logger)
    {
        _logger = logger;
        var value = options.Value;
        _current = value.Enabled ? Hash(value.Secret!) : null;

        // With no next secret, a random one that nothing can send: the comparison still happens.
        _next = string.IsNullOrEmpty(value.NextSecret) ? RandomNumberGenerator.GetBytes(SHA256.HashSizeInBytes) : Hash(value.NextSecret);
    }

    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (_current is null || IsLivenessCheck(context.Request))
        {
            return next(context);
        }

        var sent = context.Request.Headers[ProxyOptions.SecretHeaderName];
        if (sent.Count == 1 && sent[0] is { Length: > 0 } secret && Accepts(Hash(secret)))
        {
            UseClientAddress(context);
            UseClientLocation(context);

            return next(context);
        }

        LogRefused(_logger, context.Request.Method, context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        return RefuseAsync(context);
    }

    private bool Accepts(byte[] sent) =>
        CryptographicOperations.FixedTimeEquals(sent, _current!) | CryptographicOperations.FixedTimeEquals(sent, _next);

    private static bool IsLivenessCheck(HttpRequest request) =>
        (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        && string.Equals(request.Path.Value?.TrimEnd('/'), HardeningExtensions.LivePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>One address, as the proxy wrote it. Anything else (a list, text, nothing) is ignored and the connection's own address stands.</summary>
    private static void UseClientAddress(HttpContext context)
    {
        var sent = context.Request.Headers[ProxyOptions.ClientIpHeaderName];
        // The dot or colon rules out the short forms .NET also accepts ("1" is 0.0.0.1).
        if (sent.Count == 1 && sent[0] is { Length: > 0 and <= 45 } text && text.AsSpan().IndexOfAny('.', ':') >= 0 && IPAddress.TryParse(text, out var address))
        {
            context.Connection.RemoteIpAddress = address;
        }
    }

    /// <summary>The city and country the proxy sent, kept for the rest of the request. Only ever read here, after the secret was accepted.</summary>
    private static void UseClientLocation(HttpContext context)
    {
        var sent = context.Request.Headers[ClientLocation.HeaderName];
        if (sent.Count == 1 && ClientLocation.Parse(sent[0]) is { } location)
        {
            context.Items[ClientLocation.ItemKey] = location;
        }
    }

    private static async Task RefuseAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;

        var written = await context.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = StatusCodes.Status403Forbidden, Title = "Forbidden" },
        });

        if (!written)
        {
            await context.Response.CompleteAsync();
        }
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Method} request from {Address} did not carry the proxy secret and was refused")]
    private static partial void LogRefused(ILogger logger, string method, string address);
}

public static class ProxyGateExtensions
{
    public static IServiceCollection AddProxyGate(this IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<ProxyOptions>, ProxyOptionsValidator>();
        services.AddOptions<ProxyOptions>().BindConfiguration(ProxyOptions.SectionName).ValidateOnStart();
        services.AddSingleton<ProxyGateMiddleware>();

        return services;
    }
}
