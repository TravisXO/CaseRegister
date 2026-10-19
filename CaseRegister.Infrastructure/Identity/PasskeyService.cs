using System.Buffers.Text;
using System.Security.Claims;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Application.Notifications;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Notifications;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Passkeys. TOTP is always there as the fallback: nothing here touches the authenticator, so removing every passkey leaves the account exactly
/// as it was before the first was added. A passkey is a credential, not a record, so removing one deletes its row (like a session), and the audit
/// log and a security notice are what remember that it happened.
/// </summary>
internal sealed class PasskeyService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    AppDbContext db,
    PasskeyCeremonies ceremonies,
    IStepUpVerifier stepUps,
    SignInThrottle throttle,
    SignInHistory history,
    INotificationService notifications,
    IClientLocationAccessor location,
    IAuditLog audit,
    Microsoft.Extensions.Logging.ILogger<PasskeyService> logger) : IPasskeyService
{
    public const int MaxPerPerson = 10;
    public const int MaxNameLength = 60;

    public async Task<IReadOnlyList<PasskeyView>> ListAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return [];
        }

        return [.. (await users.GetPasskeysAsync(user)).OrderBy(passkey => passkey.CreatedAt).Select(View)];
    }

    public async Task<PasskeyOptionsResult?> BeginRegistrationAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await UsableAsync(userId);
        if (user is null || (await users.GetPasskeysAsync(user)).Count >= MaxPerPerson)
        {
            return null;
        }

        return new PasskeyOptionsResult(await ceremonies.StartCreationAsync(user, cancellationToken) ?? string.Empty);
    }

    public async Task<PasskeyResult> CompleteRegistrationAsync(Guid userId, string credentialJson, string name, StepUp stepUp, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var user = await UsableAsync(userId);
        if (user is null)
        {
            return new PasskeyResult(PasskeyOutcome.NotAvailable);
        }

        // The browser's answer first: it costs nothing, so a bad one does not use up the person's authenticator code.
        var passkey = await ceremonies.FinishCreationAsync(userId, credentialJson, cancellationToken);
        if (passkey is null)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(AuditAction.PasskeyRejected, userId, userId.ToString(), remoteIpAddress, "adding a passkey: not verified"), logger, cancellationToken);

            return new PasskeyResult(PasskeyOutcome.Failed);
        }

        var step = await stepUps.VerifyAsync(userId, stepUp, remoteIpAddress, cancellationToken);
        if (step.Outcome != TwoFactorOutcome.Succeeded)
        {
            return step.Outcome == TwoFactorOutcome.RateLimited ? new PasskeyResult(PasskeyOutcome.RateLimited, step.RetryAfter) : new PasskeyResult(PasskeyOutcome.StepUpFailed);
        }

        var existing = await users.GetPasskeysAsync(user);
        if (existing.Count >= MaxPerPerson || existing.Any(other => other.CredentialId.AsSpan().SequenceEqual(passkey.CredentialId)))
        {
            return new PasskeyResult(PasskeyOutcome.Failed);
        }

        passkey.Name = CleanName(name);
        var saved = await users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!saved.Succeeded)
        {
            return new PasskeyResult(PasskeyOutcome.Failed);
        }

        await audit.WriteAsync(
            new AuditEntry(AuditAction.PasskeyAdded, userId, userId.ToString(), remoteIpAddress, $"passkey {existing.Count + 1} of {MaxPerPerson} (user verified)", TargetType: "User"),
            cancellationToken);
        await notifications.NotifyAsync([NotificationFactory.PasskeyAdded(userId, Guid.CreateVersion7(), location.Current)], cancellationToken);

        return new PasskeyResult(PasskeyOutcome.Succeeded, Passkey: View(passkey));
    }

    public async Task<PasskeyResult> RenameAsync(Guid userId, string id, string name, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var user = await UsableAsync(userId);
        if (user is null)
        {
            return new PasskeyResult(PasskeyOutcome.NotAvailable);
        }

        var passkey = await FindAsync(user, id);
        if (passkey is null)
        {
            return new PasskeyResult(PasskeyOutcome.NotFound);
        }

        passkey.Name = CleanName(name);
        if (!(await users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded)
        {
            return new PasskeyResult(PasskeyOutcome.Failed);
        }

        await audit.WriteAsync(new AuditEntry(AuditAction.PasskeyRenamed, userId, userId.ToString(), remoteIpAddress, TargetType: "User"), cancellationToken);

        return new PasskeyResult(PasskeyOutcome.Succeeded, Passkey: View(passkey));
    }

    public async Task<PasskeyResult> RemoveAsync(Guid userId, string id, StepUp stepUp, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var user = await UsableAsync(userId);
        if (user is null)
        {
            return new PasskeyResult(PasskeyOutcome.NotAvailable);
        }

        var passkey = await FindAsync(user, id);
        if (passkey is null)
        {
            return new PasskeyResult(PasskeyOutcome.NotFound);
        }

        var step = await stepUps.VerifyAsync(userId, stepUp, remoteIpAddress, cancellationToken);
        if (step.Outcome != TwoFactorOutcome.Succeeded)
        {
            return step.Outcome == TwoFactorOutcome.RateLimited ? new PasskeyResult(PasskeyOutcome.RateLimited, step.RetryAfter) : new PasskeyResult(PasskeyOutcome.StepUpFailed);
        }

        if (!(await users.RemovePasskeyAsync(user, passkey.CredentialId)).Succeeded)
        {
            return new PasskeyResult(PasskeyOutcome.Failed);
        }

        var remaining = (await users.GetPasskeysAsync(user)).Count;
        await audit.WriteAsync(
            new AuditEntry(AuditAction.PasskeyRemoved, userId, userId.ToString(), remoteIpAddress, $"{remaining} passkeys left; the authenticator app still works", TargetType: "User"),
            cancellationToken);

        // Someone may have used that passkey: every other session ends, and this one carries on with a new security stamp.
        SessionEnding.Ensure(await users.UpdateSecurityStampAsync(user));
        await SessionEnding.ReissueAndEndOthersAsync(signInManager, db, user, cancellationToken);
        await notifications.NotifyAsync([NotificationFactory.PasskeyRemoved(userId, Guid.CreateVersion7(), location.Current)], cancellationToken);

        return new PasskeyResult(PasskeyOutcome.Succeeded);
    }

    public async Task<PasskeyOptionsResult> BeginSignInAsync(CancellationToken cancellationToken = default) =>
        new(await ceremonies.StartRequestAsync(null, cancellationToken));

    public async Task<PasskeyResult> CompleteSignInAsync(string credentialJson, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var proven = await ceremonies.FinishRequestAsync(credentialJson, null, cancellationToken);
        if (proven is null)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(AuditAction.SignInFailed, IpAddress: remoteIpAddress, Detail: "passkey not accepted"), logger, cancellationToken);

            return new PasskeyResult(PasskeyOutcome.Failed);
        }

        var user = proven.Value.User;

        // The same gates as a password sign-in: approved, not locked out, and two-factor really set up (a passkey is added on top of it, so
        // an account in the middle of a reset, which has none, cannot get in this way).
        var usable = await signInManager.CanSignInAsync(user)
            && await throttle.LockedUntilAsync(user.Id, remoteIpAddress, cancellationToken) is null
            && await db.Authenticators.AnyAsync(record => record.UserId == user.Id && record.EnabledAt != null, cancellationToken)
            && !await db.EnrolmentCodes.AnyAsync(code => code.UserId == user.Id && code.UsedAt == null && code.RevokedAt == null, cancellationToken);
        if (!usable)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(AuditAction.SignInFailed, user.Id, IpAddress: remoteIpAddress, Detail: "passkey: account not available"), logger, cancellationToken);

            return new PasskeyResult(PasskeyOutcome.Failed);
        }

        await signInManager.Context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);

        // The passkey verified the person (something they have and something they are or know), so this is a multi-factor sign-in on its own.
        await signInManager.SignInWithClaimsAsync(
            user,
            new AuthenticationProperties { IsPersistent = false },
            [new Claim(AuthenticationClaims.Amr, AuthenticationClaims.AmrMultiFactor)]);

        await audit.WriteAsync(new AuditEntry(AuditAction.SignInSucceeded, user.Id, IpAddress: remoteIpAddress, Detail: "passkey (user verified)"), cancellationToken);
        await throttle.RecordFullSignInAsync(user.Id, remoteIpAddress, cancellationToken);
        await history.RecordAsync(user.Id, cancellationToken);
        await notifications.EnsureTimeBasedAsync(user.Id, cancellationToken);

        return new PasskeyResult(PasskeyOutcome.Succeeded);
    }

    public async Task<PasskeyOptionsResult?> BeginStepUpAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await UsableAsync(userId);
        if (user is null || (await users.GetPasskeysAsync(user)).Count == 0)
        {
            return null;
        }

        return new PasskeyOptionsResult(await ceremonies.StartRequestAsync(user, cancellationToken));
    }

    // --- Internals -------------------------------------------------------------------------------------------------------

    private async Task<ApplicationUser?> UsableAsync(Guid userId)
    {
        var user = await users.FindByIdAsync(userId.ToString());

        return user is not null && await signInManager.CanSignInAsync(user) ? user : null;
    }

    private async Task<UserPasskeyInfo?> FindAsync(ApplicationUser user, string id)
    {
        byte[] credentialId;
        try
        {
            credentialId = Base64Url.DecodeFromChars(id);
        }
        catch (FormatException)
        {
            return null;
        }

        // Looked up among this person's own passkeys, so somebody else's id looks exactly like one that does not exist.
        return await users.GetPasskeyAsync(user, credentialId);
    }

    internal static string CleanName(string name)
    {
        var cleaned = new string([.. (name ?? string.Empty).Where(character => !char.IsControl(character))]).Trim();

        return cleaned.Length == 0 ? "Passkey" : cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength].TrimEnd() : cleaned;
    }

    internal static PasskeyView View(UserPasskeyInfo passkey) =>
        new(Base64Url.EncodeToString(passkey.CredentialId), string.IsNullOrWhiteSpace(passkey.Name) ? "Passkey" : passkey.Name, passkey.CreatedAt, passkey.IsBackedUp, passkey.Transports ?? []);
}

/// <summary>
/// The second proof for a sensitive act, from a code (authenticator or recovery) or from a passkey. A passkey counts only if it verified the
/// user; the answer is checked against a challenge the server made for this person, so an old answer is no use.
/// </summary>
internal sealed class StepUpVerifier(
    ITwoFactorService twoFactor,
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    PasskeyCeremonies ceremonies,
    TwoFactorAttemptLimiter limiter,
    IAuditLog audit,
    Microsoft.Extensions.Logging.ILogger<StepUpVerifier> logger) : IStepUpVerifier
{
    public async Task<TwoFactorResult> VerifyAsync(Guid userId, StepUp stepUp, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        if (!stepUp.IsValidShape)
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        if (!string.IsNullOrWhiteSpace(stepUp.Code))
        {
            return await twoFactor.VerifyCodeAsync(userId, stepUp.Code, remoteIpAddress, cancellationToken);
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        if (!limiter.TryAcquire(userId, out var retryAfter))
        {
            return new TwoFactorResult(TwoFactorOutcome.RateLimited, retryAfter);
        }

        var proven = await ceremonies.FinishRequestAsync(stepUp.PasskeyAssertion!, userId, cancellationToken);
        if (proven is null)
        {
            await audit.WriteStandaloneAsync(new AuditEntry(AuditAction.TwoFactorFailed, userId, IpAddress: remoteIpAddress, Detail: "invalid passkey"), logger, cancellationToken);

            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        return new TwoFactorResult(TwoFactorOutcome.Succeeded);
    }
}
