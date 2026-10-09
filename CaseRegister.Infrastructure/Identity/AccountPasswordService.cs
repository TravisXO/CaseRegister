using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class AccountPasswordService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    LockoutTracker lockouts,
    IAuditLog audit) : IAccountPasswordService
{
    private const string IncorrectCurrentPassword = "The current password is incorrect.";

    public async Task<ChangePasswordResult> ChangePasswordAsync(
        Guid userId,
        ChangePasswordCommand command,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
        {
            await audit.WriteAsync(
                new AuditEntry(AuditAction.PasswordChangeFailed, userId, IpAddress: remoteIpAddress, Detail: "account not available"),
                cancellationToken);
            return ChangePasswordResult.Failure(IncorrectCurrentPassword);
        }

        // Guessing the current password from a stolen session counts towards lockout like any other guess.
        var check = await signInManager.CheckPasswordSignInAsync(user, command.CurrentPassword, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            if (check.IsLockedOut)
            {
                await lockouts.RecordLockoutAsync(user, remoteIpAddress, cancellationToken);
            }

            await audit.WriteAsync(
                new AuditEntry(AuditAction.PasswordChangeFailed, user.Id, IpAddress: remoteIpAddress, Detail: "wrong current password"),
                cancellationToken);
            return ChangePasswordResult.Failure(IncorrectCurrentPassword);
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
