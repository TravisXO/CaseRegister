namespace CaseRegister.Application.Auth;

/// <param name="CurrentPassword">Required: a stolen session alone must not be enough to take over an account.</param>
/// <param name="NewPassword">Checked against the password policy and known breaches.</param>
public sealed record ChangePasswordCommand(string CurrentPassword, string NewPassword);

/// <param name="Succeeded">Whether the password was changed.</param>
/// <param name="Errors">Safe-to-show reasons when it was not. They never include either password.</param>
public sealed record ChangePasswordResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static ChangePasswordResult Success { get; } = new(true, []);

    public static ChangePasswordResult Failure(params string[] errors) => new(false, errors);
}

public interface IAccountPasswordService
{
    Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId,
        ChangePasswordCommand command,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default);
}
