namespace CaseRegister.Application.Auth;

/// <param name="Email">The account's email address, which is its username.</param>
/// <param name="Password">The password the user typed. Never logged.</param>
/// <param name="TurnstileToken">The token from the Cloudflare Turnstile challenge on the sign-in page.</param>
public sealed record SignInCommand(string Email, string Password, string? TurnstileToken);
