namespace CaseRegister.Application.Auth;

/// <summary>Verifies a Cloudflare Turnstile bot-protection token.</summary>
public interface ITurnstileVerifier
{
    /// <summary>True only when Cloudflare confirms the token. Any error or missing token is false.</summary>
    Task<bool> VerifyAsync(string? token, string? remoteIpAddress, CancellationToken cancellationToken = default);
}
