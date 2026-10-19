namespace CaseRegister.Application.Auth;

/// <param name="CurrentPassword">Required: a stolen session alone must not be enough to take over an account.</param>
/// <param name="NewPassword">Checked against the password policy and known breaches.</param>
/// <param name="TwoFactorCode">Required as well, unless a passkey answer is given instead: a current authenticator code (or an unused recovery code).</param>
/// <param name="PasskeyAssertion">A passkey answer (JSON) to the options from <c>/api/auth/passkeys/step-up/options</c>, instead of a code.</param>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword, string? TwoFactorCode = null, string? PasskeyAssertion = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public StepUp StepUp => new(TwoFactorCode, PasskeyAssertion);
}

/// <param name="Succeeded">Whether the password was changed.</param>
/// <param name="Errors">Safe-to-show reasons when it was not. They never include any password or code.</param>
/// <param name="RetryAfter">Set when too many two-factor attempts were made: how long to wait.</param>
public sealed record ChangePasswordResult(bool Succeeded, IReadOnlyList<string> Errors, TimeSpan? RetryAfter = null)
{
    public static ChangePasswordResult Success { get; } = new(true, []);

    public static ChangePasswordResult Failure(params string[] errors) => new(false, errors);

    public static ChangePasswordResult TooManyAttempts(TimeSpan retryAfter) =>
        new(false, ["Too many attempts. Please wait and try again."], retryAfter);
}

public interface IAccountPasswordService
{
    Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId,
        ChangePasswordCommand command,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default);
}
