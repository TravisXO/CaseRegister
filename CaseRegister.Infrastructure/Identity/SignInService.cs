using System.Security.Claims;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class SignInService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    AppDbContext db,
    ITurnstileVerifier turnstile,
    DummyPasswordVerifier dummyVerifier,
    CredentialChecker credentials,
    SignInThrottle throttle,
    IAuditLog audit,
    Microsoft.Extensions.Logging.ILogger<SignInService> logger) : ISignInService
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

        // Not approved, or this address (or the whole account) is locked out: refuse without checking the password, but spend the
        // same time a check would have taken. A lockout of some other address does not come into it.
        if (!await signInManager.CanSignInAsync(user) || await throttle.LockedUntilAsync(user.Id, remoteIpAddress, cancellationToken) is not null)
        {
            return await FailAsync(command.Password, user.Id, subject, remoteIpAddress, "account not available", cancellationToken);
        }

        if (!await credentials.VerifyPasswordAsync(user, command.Password, remoteIpAddress, cancellationToken))
        {
            await audit.WriteStandaloneAsync(
                new AuditEntry(AuditAction.SignInFailed, user.Id, subject, remoteIpAddress, "wrong password"),
                logger, cancellationToken);
            return SignInStatus.Failed;
        }

        // The password is right. What happens next depends on whether the account has a second factor.
        var context = signInManager.Context;
        var twoFactorOn = await db.Authenticators.AnyAsync(a => a.UserId == user.Id && a.EnabledAt != null, cancellationToken);

        if (twoFactorOn)
        {
            // No session yet. A short-lived cookie remembers who passed the password step, for the code that follows.
            var step = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
                IdentityConstants.TwoFactorUserIdScheme));

            await context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, step, new AuthenticationProperties { IsPersistent = false });

            return SignInStatus.TwoFactorRequired;
        }

        // After a reset the one-time enrolment code comes first. The same short-lived step cookie remembers who passed the password.
        if (await db.EnrolmentCodes.AnyAsync(code => code.UserId == user.Id && code.CompletedAt == null && code.RevokedAt == null, cancellationToken))
        {
            var codeStep = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())],
                IdentityConstants.TwoFactorUserIdScheme));

            await context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, codeStep, new AuthenticationProperties { IsPersistent = false });

            return SignInStatus.EnrolmentCodeRequired;
        }

        // Two-factor is mandatory but not set up yet: a restricted session ("pwd" only), which the
        // authorisation policies allow to reach enrolment and nothing else.
        await signInManager.SignInWithClaimsAsync(
            user,
            new AuthenticationProperties { IsPersistent = false },
            [new Claim(AuthenticationClaims.Amr, AuthenticationClaims.AmrPassword)]);

        await audit.WriteAsync(
            new AuditEntry(AuditAction.SignInSucceeded, user.Id, subject, remoteIpAddress, "password only: two-factor enrolment required"),
            cancellationToken);

        return SignInStatus.TwoFactorEnrolmentRequired;
    }

    public Task SignOutAsync() =>
        // Ends the server-side session at once (the cookie handler removes its row) and clears any two-factor step.
        signInManager.SignOutAsync();

    private async Task<SignInStatus> FailAsync(
        string password,
        Guid? userId,
        string subject,
        string? remoteIpAddress,
        string reason,
        CancellationToken cancellationToken)
    {
        dummyVerifier.Verify(password);
        await audit.WriteStandaloneAsync(new AuditEntry(AuditAction.SignInFailed, userId, subject, remoteIpAddress, reason), logger, cancellationToken);

        return SignInStatus.Failed;
    }
}
