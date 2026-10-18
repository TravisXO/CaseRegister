namespace CaseRegister.Application.Auth;

/// <summary>Verifies a Cloudflare Turnstile bot-protection token.</summary>
public interface ITurnstileVerifier
{
    /// <summary>
    /// True only when Cloudflare confirms the token, was issued for this site (the hostname in its answer is the configured one), and was issued for
    /// this form (the action in its answer is <paramref name="action"/>, set by the widget on that form): a token taken from another site, or from another
    /// form, is no use here. Any error or missing token is false.
    /// </summary>
    Task<bool> VerifyAsync(string? token, string? remoteIpAddress, string action, CancellationToken cancellationToken = default);

    /// <summary>The names the forms give their checks. The widget on each form asks for its own, and the answer has to match.</summary>
    static class Actions
    {
        public const string SignIn = "sign-in";
        public const string SignUp = "sign-up";
    }
}
