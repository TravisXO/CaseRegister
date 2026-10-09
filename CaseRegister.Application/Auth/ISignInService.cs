namespace CaseRegister.Application.Auth;

/// <summary>
/// The outcome of the password step of signing in. There is deliberately only one kind of failure:
/// wrong password, unknown email, locked out, not approved and failed bot check all look the same to
/// the caller, so nothing about an account can be learned from signing in.
/// </summary>
public enum SignInStatus
{
    Failed,

    /// <summary>The password was right and the account has two-factor on: a code is needed to finish.</summary>
    TwoFactorRequired,

    /// <summary>The password was right but two-factor is not set up yet. The session can reach enrolment and nothing else.</summary>
    TwoFactorEnrolmentRequired,
}

public interface ISignInService
{
    Task<SignInStatus> SignInAsync(SignInCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default);

    Task SignOutAsync();
}
