namespace CaseRegister.Application.Auth;

/// <summary>
/// The outcome of a sign-in attempt. There is deliberately only one kind of failure: wrong password,
/// unknown email, locked out, not approved and failed bot check all look the same to the caller, so
/// nothing about an account can be learned from signing in.
/// </summary>
public enum SignInStatus
{
    Succeeded,
    Failed,
}

public interface ISignInService
{
    Task<SignInStatus> SignInAsync(SignInCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default);

    Task SignOutAsync();
}
