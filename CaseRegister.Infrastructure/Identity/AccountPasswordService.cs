using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class AccountPasswordService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    CredentialChecker credentials,
    SignInThrottle throttle,
    ITwoFactorService twoFactor,
    IAuditLog audit) : IAccountPasswordService
{
    private const string IncorrectCurrentPassword = "The current password is incorrect.";
    private const string IncorrectCode = "The two-factor code is not valid.";

    public async Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId,
        ChangePasswordCommand command,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || await throttle.LockedUntilAsync(userId, remoteIpAddress, cancellationToken) is not null)
        {
            await audit.WriteAsync(
                new AuditEntry(AuditAction.PasswordChangeFailed, userId, IpAddress: remoteIpAddress, Detail: "account not available"),
                cancellationToken);
            return ChangePasswordResult.Failure(IncorrectCurrentPassword);
        }

        // Guessing the current password from a stolen session counts towards the lockout like any other guess.
        if (!await credentials.VerifyPasswordAsync(user, command.CurrentPassword, remoteIpAddress, cancellationToken))
        {
            await audit.WriteAsync(
                new AuditEntry(AuditAction.PasswordChangeFailed, user.Id, IpAddress: remoteIpAddress, Detail: "wrong current password"),
                cancellationToken);
            return ChangePasswordResult.Failure(IncorrectCurrentPassword);
        }

        // And a current second factor as well: the password alone is not enough to change the password.
        var second = await twoFactor.VerifyCodeAsync(userId, command.TwoFactorCode, remoteIpAddress, cancellationToken);
        if (second.Outcome == TwoFactorOutcome.RateLimited)
        {
            return ChangePasswordResult.TooManyAttempts(second.RetryAfter ?? TimeSpan.FromMinutes(1));
        }

        if (second.Outcome != TwoFactorOutcome.Succeeded)
        {
            await audit.WriteAsync(
                new AuditEntry(AuditAction.PasswordChangeFailed, user.Id, IpAddress: remoteIpAddress, Detail: "wrong two-factor code"),
                cancellationToken);
            return ChangePasswordResult.Failure(IncorrectCode);
        }

        // Validates the new password (policy, personal details, breaches) and, on success, gives the
        // account a new security stamp so every other session ends.
        var changed = await users.ChangePasswordAsync(user, command.CurrentPassword, command.NewPassword);
        if (!changed.Succeeded)
        {
            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.PasswordChangeFailed,
                    user.Id,
                    IpAddress: remoteIpAddress,
                    Detail: string.Join(',', changed.Errors.Select(error => error.Code))),
                cancellationToken);
            return ChangePasswordResult.Failure([.. changed.Errors.Select(error => error.Description)]);
        }

        // The stamp changed, so this session would be signed out too. Re-issue its cookie on the new stamp.
        await signInManager.RefreshSignInAsync(user);
        await audit.WriteAsync(new AuditEntry(AuditAction.PasswordChanged, user.Id, IpAddress: remoteIpAddress), cancellationToken);

        return ChangePasswordResult.Success;
    }
}
