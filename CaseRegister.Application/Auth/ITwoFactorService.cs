namespace CaseRegister.Application.Auth;

/// <param name="Code">A 6-digit authenticator code or, where allowed, a recovery code.</param>
public sealed record TwoFactorCodeCommand(string Code);

public enum TwoFactorOutcome
{
    Succeeded,

    /// <summary>The code was wrong, a replay, expired, or the account could not use it. They all look the same.</summary>
    Failed,

    /// <summary>Too many attempts for this user in the last minute. The code was not even checked.</summary>
    RateLimited,

    /// <summary>There is nothing to confirm (no enrolment in progress).</summary>
    NotApplicable,

    /// <summary>Ten wrong authenticator codes in a row: the authenticator is locked and only a recovery code gets in. The code was not checked.</summary>
    AuthenticatorLocked,
}

/// <param name="RetryAfter">Set when <see cref="TwoFactorOutcome.RateLimited"/>: how long to wait.</param>
/// <param name="RecoveryCodes">Set once, on enrolment or regeneration. They cannot be shown again.</param>
public sealed record TwoFactorResult(
    TwoFactorOutcome Outcome,
    TimeSpan? RetryAfter = null,
    IReadOnlyList<string>? RecoveryCodes = null);

/// <param name="OtpAuthUri">For the QR code (issuer CaseRegister).</param>
/// <param name="SharedKey">The same key in base32, for typing in by hand.</param>
public sealed record TwoFactorEnrolmentStart(string OtpAuthUri, string SharedKey);

/// <summary>Two-factor authentication (TOTP), which every account must have.</summary>
public interface ITwoFactorService
{
    /// <summary>Makes a new key for the account. Null if two-factor is already switched on.</summary>
    Task<TwoFactorEnrolmentStart?> StartEnrolmentAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Switches two-factor on, but only after a valid code, and returns the recovery codes once.</summary>
    Task<TwoFactorResult> ConfirmEnrolmentAsync(Guid userId, string code, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>Completes a sign-in that has passed the password step. Takes an authenticator code or a recovery code.</summary>
    Task<TwoFactorResult> VerifySignInAsync(string code, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a code for someone who is already signed in, before a sensitive action such as changing
    /// their password. An authenticator code is good once; a recovery code is used up.
    /// </summary>
    Task<TwoFactorResult> VerifyCodeAsync(Guid userId, string code, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// After a reset: checks the one-time enrolment code for somebody who has passed the password step, and opens the restricted session
    /// that can reach enrolment. The code is not used up by this: it is, when enrolment is finished.
    /// </summary>
    Task<TwoFactorResult> RedeemEnrolmentCodeAsync(string code, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>Replaces all recovery codes. Needs a valid authenticator code.</summary>
    Task<TwoFactorResult> RegenerateRecoveryCodesAsync(Guid userId, string code, string? remoteIpAddress, CancellationToken cancellationToken = default);
}
