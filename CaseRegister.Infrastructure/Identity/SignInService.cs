using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class SignInService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    ITurnstileVerifier turnstile,
    DummyPasswordVerifier dummyVerifier,
    LockoutTracker lockouts,
    IAuditLog audit) : ISignInService
{
    public async Task<SignInStatus> SignInAsync(SignInCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var email = EmailNormaliser.Normalise(command.Email);
        var subject = AuditSubject.ForAttemptedEmail(email);

        // Bot check first. A failure looks exactly like any other failed sign-in.
        if (!await turnstile.VerifyAsync(command.TurnstileToken, remoteIpAddress, cancellationToken))
        {
            return await FailAsync(command.Password, null, subject, remoteIpAddress, "bot check failed", cancellationToken);
        }

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return await FailAsync(command.Password, null, subject, remoteIpAddress, "unknown account", cancellationToken);
        }

        // Not approved, or already locked out: refuse without checking the password, but spend the
        // same time a check would have taken.
        if (!await signInManager.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
        {
            return await FailAsync(command.Password, user.Id, subject, remoteIpAddress, "account not available", cancellationToken);
        }

        var check = await signInManager.CheckPasswordSignInAsync(user, command.Password, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            // The account was not locked before this attempt, so a lockout now means this attempt caused it.
            if (check.IsLockedOut)
            {
                await lockouts.RecordLockoutAsync(user, remoteIpAddress, cancellationToken);
            }

            await audit.WriteAsync(
                new AuditEntry(AuditAction.SignInFailed, user.Id, subject, remoteIpAddress, "wrong password"),
                cancellationToken);
            return SignInStatus.Failed;
        }

        // Never persistent: the session cookie ends when the browser closes (see the cookie settings).
        await signInManager.SignInAsync(user, isPersistent: false);
        await audit.WriteAsync(new AuditEntry(AuditAction.SignInSucceeded, user.Id, subject, remoteIpAddress), cancellationToken);

        return SignInStatus.Succeeded;
    }

    public Task SignOutAsync() => signInManager.SignOutAsync();

    private async Task<SignInStatus> FailAsync(
        string password,
        Guid? userId,
        string subject,
        string? remoteIpAddress,
        string reason,
        CancellationToken cancellationToken)
    {
        dummyVerifier.Verify(password);
        await audit.WriteAsync(new AuditEntry(AuditAction.SignInFailed, userId, subject, remoteIpAddress, reason), cancellationToken);

        return SignInStatus.Failed;
    }
}
