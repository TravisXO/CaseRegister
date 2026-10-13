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
}

public sealed partial class TurnstileVerifier(
    HttpClient httpClient,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileVerifier> logger) : ITurnstileVerifier
{
    public const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public async Task<bool> VerifyAsync(string? token, string? remoteIpAddress, CancellationToken cancellationToken = default)
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

            return result?.Success == true;
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

    private sealed record SiteVerifyResponse([property: JsonPropertyName("success")] bool Success);
}
