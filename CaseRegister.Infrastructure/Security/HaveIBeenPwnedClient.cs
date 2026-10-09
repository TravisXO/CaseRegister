using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using CaseRegister.Application.Auth;
using Microsoft.Extensions.Logging;

namespace CaseRegister.Infrastructure.Security;

/// <summary>
/// Checks a password against Have I Been Pwned's range API (k-anonymity): only the first five
/// characters of the password's SHA-1 hash leave this server, never the password or the full hash.
/// </summary>
public sealed partial class HaveIBeenPwnedClient(HttpClient httpClient, ILogger<HaveIBeenPwnedClient> logger) : IBreachedPasswordChecker
{
    public const string BaseAddress = "https://api.pwnedpasswords.com/";
    private const int PrefixLength = 5;

    [SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "The Have I Been Pwned range protocol is defined in terms of SHA-1. It is a lookup key here, not a security control.")]
    public async Task<BreachCheckResult> CheckAsync(string password, CancellationToken cancellationToken = default)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var prefix = hash[..PrefixLength];
        var suffix = hash[PrefixLength..];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"range/{prefix}");

            // Padding hides how many real matches a prefix has, from anyone watching the traffic.
            request.Headers.Add("Add-Padding", "true");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable(logger, $"HTTP {(int)response.StatusCode}");
                return BreachCheckResult.Unavailable;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return ContainsMatch(body, suffix) ? BreachCheckResult.Breached : BreachCheckResult.NotBreached;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            LogUnavailable(logger, ex.GetType().Name);
            return BreachCheckResult.Unavailable;
        }
    }

    // Each line is "<hash suffix>:<count>". Padding lines have a count of 0 and are not real matches.
    private static bool ContainsMatch(string body, string suffix)
    {
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2
                && string.Equals(parts[0], suffix, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var count)
                && count > 0)
            {
                return true;
            }
        }

        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Have I Been Pwned could not be reached: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
