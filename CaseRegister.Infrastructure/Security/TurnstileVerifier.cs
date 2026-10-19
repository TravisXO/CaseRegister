using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CaseRegister.Application.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaseRegister.Infrastructure.Security;

public sealed class TurnstileOptions
{
    public const string SecretKeyKey = "Turnstile:SecretKey";

    /// <summary>
    /// The Turnstile secret key. Cloudflare's published test secrets work for Development and tests;
    /// production uses a real one from the environment.
    /// </summary>
    public string? SecretKey { get; set; }

    public const string ExpectedHostnameKey = "Turnstile:ExpectedHostname";
    public const string RequireActionKey = "Turnstile:RequireAction";

    /// <summary>
    /// The hostname of the site the forms are on (the Pages hostname). Cloudflare says in its answer which hostname the token was issued for;
    /// a token for any other site is refused. Required in Production. Left empty, the check is skipped, which is only right for Cloudflare's test keys
    /// (they always answer "example.com").
    /// </summary>
    public string? ExpectedHostname { get; set; }

    /// <summary>
    /// Whether the action in Cloudflare's answer has to be the form's own. On, except with Cloudflare's test keys, which do not echo an action
    /// (so Development turns it off). Production refuses to start with it off.
    /// </summary>
    public bool RequireAction { get; set; } = true;
}

public sealed partial class TurnstileVerifier(
    HttpClient httpClient,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    public const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public async Task<bool> VerifyAsync(string? token, string? remoteIpAddress, string action, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var form = new Dictionary<string, string>
        {
            ["secret"] = options.Value.SecretKey ?? string.Empty,
            ["response"] = token,
        };
        if (!string.IsNullOrWhiteSpace(remoteIpAddress))
        {
            form["remoteip"] = remoteIpAddress;
        }

        try
        {
            using var response = await httpClient.PostAsync(VerifyUrl, new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogFailure(logger, $"HTTP {(int)response.StatusCode}");
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);

            if (result?.Success != true)
            {
                return false;
            }

            // A genuine token, but for another site or another form: of no use here.
            if (!string.IsNullOrWhiteSpace(options.Value.ExpectedHostname)
                && !string.Equals(result.Hostname, options.Value.ExpectedHostname.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                LogFailure(logger, "the token was issued for a different hostname");
                return false;
            }

            if (options.Value.RequireAction && !string.Equals(result.Action, action, StringComparison.Ordinal))
            {
                LogFailure(logger, "the token was issued for a different form");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // If Cloudflare cannot be reached nobody gets through: fail closed.
            LogFailure(logger, ex.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Turnstile verification failed: {Reason}")]
    private static partial void LogFailure(ILogger logger, string reason);

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("hostname")] string? Hostname = null,
        [property: JsonPropertyName("action")] string? Action = null);
}
