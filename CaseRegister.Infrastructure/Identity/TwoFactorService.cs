using System.Security.Claims;
using System.Security.Cryptography;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Application.Notifications;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class TwoFactorService(
    UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager,
    AppDbContext db,
    AuthenticatorKeyProtector keyProtector,
    TwoFactorAttemptLimiter limiter,
    SignInThrottle throttle,
    INotificationService notifications,
    IAuditLog audit,
    Microsoft.Extensions.Logging.ILogger<TwoFactorService> logger,
    TimeProvider timeProvider) : ITwoFactorService
{
    /// <summary>The name an authenticator app shows for this service.</summary>
    public const string Issuer = "CaseRegister";

    private sealed record Check(TwoFactorOutcome Outcome, TimeSpan? RetryAfter = null, bool UsedRecoveryCode = false);

    // --- Enrolment ---------------------------------------------------------------------------

    public async Task<TwoFactorEnrolmentStart?> StartEnrolmentAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user))
        {
            return null;
        }

        var record = await db.Authenticators.SingleOrDefaultAsync(r => r.UserId == userId, cancellationToken);
        if (record?.EnabledAt is not null)
        {
            return null; // already on: changing it needs the admin process, not a new key from a session
        }

        // 160 random bits, as RFC 4226 recommends. Starting again replaces a key that was never confirmed.
        var key = RandomNumberGenerator.GetBytes(Totp.KeyBytes);
        var encrypted = keyProtector.Protect(key);

        if (record is null)
        {
            db.Authenticators.Add(new AuthenticatorRecord(userId, encrypted));
        }
        else
        {
            record.EncryptedKey = encrypted;
            record.LastUsedStep = null;
        }

        await db.SaveChangesAsync(cancellationToken);

        var sharedKey = Base32.Encode(key);

        return new TwoFactorEnrolmentStart(Totp.BuildOtpAuthUri(Issuer, user.Email!, sharedKey), sharedKey);
    }

    public async Task<TwoFactorResult> ConfirmEnrolmentAsync(
        Guid userId,
        string code,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        var record = await db.Authenticators.SingleOrDefaultAsync(r => r.UserId == userId, cancellationToken);
        if (record is null || record.EnabledAt is not null)
        {
            return new TwoFactorResult(TwoFactorOutcome.NotApplicable);
        }

        // Two-factor is only switched on by a valid code, which proves the app really holds the key.
        var check = await CheckAsync(user, record, code, remoteIpAddress, allowRecoveryCode: false, enableOnSuccess: true, cancellationToken);
        if (check.Outcome != TwoFactorOutcome.Succeeded)
        {
            return new TwoFactorResult(check.Outcome, check.RetryAfter);
        }

        user.TwoFactorEnabled = true;
        await users.UpdateAsync(user);

        var codes = await CreateRecoveryCodesAsync(userId, cancellationToken);
        await audit.WriteAsync(new AuditEntry(AuditAction.TwoFactorEnrolled, userId, IpAddress: remoteIpAddress), cancellationToken);

        // The session was restricted to enrolment. Now it has passed a second factor, so it is upgraded.
        await UpgradeSessionAsync(user);
        await throttle.RecordFullSignInAsync(userId, remoteIpAddress, cancellationToken);
        await notifications.EnsureTimeBasedAsync(userId, cancellationToken);

        return new TwoFactorResult(TwoFactorOutcome.Succeeded, RecoveryCodes: codes);
    }

    // --- Using a code ------------------------------------------------------------------------

    public async Task<TwoFactorResult> VerifySignInAsync(string code, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        var context = signInManager.Context;

        // Who passed the password step is remembered in a short-lived cookie, and nothing else is.
        var step = await context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        var userIdText = step.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!step.Succeeded || !Guid.TryParse(userIdText, out var userId))
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || await throttle.LockedUntilAsync(userId, remoteIpAddress, cancellationToken) is not null)
        {
            await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        var record = await db.Authenticators.SingleOrDefaultAsync(r => r.UserId == userId && r.EnabledAt != null, cancellationToken);
        if (record is null)
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        var check = await CheckAsync(user, record, code, remoteIpAddress, allowRecoveryCode: true, enableOnSuccess: false, cancellationToken);
        if (check.Outcome != TwoFactorOutcome.Succeeded)
        {
            return new TwoFactorResult(check.Outcome, check.RetryAfter);
        }

        await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);

        // Never persistent, and "remember this device" does not exist: every sign-in asks for a code.
        await signInManager.SignInWithClaimsAsync(
            user,
            new AuthenticationProperties { IsPersistent = false },
            [new Claim(AuthenticationClaims.Amr, AuthenticationClaims.AmrMultiFactor)]);

        await audit.WriteAsync(
            new AuditEntry(
                AuditAction.SignInSucceeded,
                userId,
                IpAddress: remoteIpAddress,
                Detail: check.UsedRecoveryCode ? "second factor: recovery code" : "second factor: authenticator code"),
            cancellationToken);

        // Fully signed in: the counters start again, and the notifications that depend on the date are made now (there is no scheduler).
        await throttle.RecordFullSignInAsync(userId, remoteIpAddress, cancellationToken);
        await notifications.EnsureTimeBasedAsync(userId, cancellationToken);

        return new TwoFactorResult(TwoFactorOutcome.Succeeded);
    }

    public async Task<TwoFactorResult> VerifyCodeAsync(
        Guid userId,
        string code,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        var record = await db.Authenticators.SingleOrDefaultAsync(r => r.UserId == userId && r.EnabledAt != null, cancellationToken);
        if (record is null)
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        var check = await CheckAsync(user, record, code, remoteIpAddress, allowRecoveryCode: true, enableOnSuccess: false, cancellationToken);

        return new TwoFactorResult(check.Outcome, check.RetryAfter);
    }

    public async Task<TwoFactorResult> RegenerateRecoveryCodesAsync(
        Guid userId,
        string code,
        string? remoteIpAddress,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !await signInManager.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        var record = await db.Authenticators.SingleOrDefaultAsync(r => r.UserId == userId && r.EnabledAt != null, cancellationToken);
        if (record is null)
        {
            return new TwoFactorResult(TwoFactorOutcome.Failed);
        }

        // An authenticator code only: a recovery code must not be able to mint more recovery codes.
        var check = await CheckAsync(user, record, code, remoteIpAddress, allowRecoveryCode: false, enableOnSuccess: false, cancellationToken);
        if (check.Outcome != TwoFactorOutcome.Succeeded)
        {
            return new TwoFactorResult(check.Outcome, check.RetryAfter);
        }

        var now = timeProvider.GetUtcNow();
        var old = await db.RecoveryCodes
            .Where(r => r.UserId == userId && r.UsedAt == null && r.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var oldCode in old)
        {
            oldCode.RevokedAt = now;
        }

        var codes = await CreateRecoveryCodesAsync(userId, cancellationToken);
        await audit.WriteAsync(new AuditEntry(AuditAction.RecoveryCodesRegenerated, userId, IpAddress: remoteIpAddress), cancellationToken);

        return new TwoFactorResult(TwoFactorOutcome.Succeeded, RecoveryCodes: codes);
    }

    // --- Internals ---------------------------------------------------------------------------

    private async Task<Check> CheckAsync(
        ApplicationUser user,
        AuthenticatorRecord record,
        string code,
        string? remoteIpAddress,
        bool allowRecoveryCode,
        bool enableOnSuccess,
        CancellationToken cancellationToken)
    {
        // First, and whatever the code turns out to be: 5 tries a minute per user, so a 6-digit code
        // cannot be brute-forced. A refused try is not checked at all and does not count towards lockout.
        if (!limiter.TryAcquire(user.Id, out var retryAfter))
        {
            return new Check(TwoFactorOutcome.RateLimited, retryAfter);
        }

        var typed = new string([.. code.Where(character => !char.IsWhiteSpace(character))]);
        var usedRecoveryCode = false;
        bool valid;

        if (Totp.LooksLikeTotpCode(typed))
        {
            // Ten wrong authenticator codes in a row lock the authenticator: only a recovery code gets in until the person signs in with one.
            // The code is not even checked, so a lucky guess cannot get past the lock.
            if (await throttle.SecondFactorLockedAsync(user.Id, cancellationToken))
            {
                return new Check(TwoFactorOutcome.AuthenticatorLocked);
            }

            valid = await TryAuthenticatorCodeAsync(record, typed, enableOnSuccess, cancellationToken);
        }
        else if (allowRecoveryCode && RecoveryCodes.LooksLikeRecoveryCode(code))
        {
            valid = usedRecoveryCode = await TryRecoveryCodeAsync(user.Id, code, cancellationToken);
        }
        else
        {
            valid = false;
        }

        if (!valid)
        {
            await throttle.RecordSecondFactorFailureAsync(user.Id, remoteIpAddress, cancellationToken);
            await audit.WriteStandaloneAsync(
                new AuditEntry(AuditAction.TwoFactorFailed, user.Id, IpAddress: remoteIpAddress, Detail: "invalid code"),
                logger, cancellationToken);

            return new Check(TwoFactorOutcome.Failed);
        }

        // A right code ends the run of wrong ones. (The password counters are cleared only by a full sign-in: see VerifySignInAsync.)
        await throttle.ClearSecondFactorFailuresAsync(user.Id, cancellationToken);

        if (usedRecoveryCode)
        {
            await audit.WriteAsync(
                new AuditEntry(AuditAction.RecoveryCodeUsed, user.Id, IpAddress: remoteIpAddress),
                cancellationToken);
        }

        return new Check(TwoFactorOutcome.Succeeded, UsedRecoveryCode: usedRecoveryCode);
    }

    private async Task<bool> TryAuthenticatorCodeAsync(
        AuthenticatorRecord record,
        string code,
        bool enableOnSuccess,
        CancellationToken cancellationToken)
    {
        var key = keyProtector.Unprotect(record.EncryptedKey);
        var now = timeProvider.GetUtcNow();

        if (!Totp.TryMatch(key, code, Totp.CurrentStep(now), record.LastUsedStep, out var step))
        {
            return false;
        }

        // Remember the step so the same code cannot be used twice. The row's version (xmin) makes this
        // safe against two requests using one code at the same instant: only one save can win.
        record.LastUsedStep = step;
        if (enableOnSuccess)
        {
            record.EnabledAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(record).State = EntityState.Detached; // the failed save must not be retried by the next one
            return false;
        }
    }

    private async Task<bool> TryRecoveryCodeAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var hash = RecoveryCodes.Hash(code);
        var record = await db.RecoveryCodes.SingleOrDefaultAsync(
            r => r.UserId == userId && r.CodeHash == hash && r.UsedAt == null && r.RevokedAt == null,
            cancellationToken);

        if (record is null)
        {
            return false;
        }

        record.UsedAt = timeProvider.GetUtcNow();

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(record).State = EntityState.Detached;
            return false; // someone used it at the same moment
        }
    }

    private async Task<IReadOnlyList<string>> CreateRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var codes = RecoveryCodes.Generate();

        foreach (var code in codes)
        {
            db.RecoveryCodes.Add(new RecoveryCodeRecord(userId, RecoveryCodes.Hash(code)));
        }

        await db.SaveChangesAsync(cancellationToken);

        return codes;
    }

    private async Task UpgradeSessionAsync(ApplicationUser user)
    {
        var context = signInManager.Context;
        var current = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        // A fresh window, but the real sign-in time is kept so the 8-hour limit is not restarted.
        var properties = new AuthenticationProperties { IsPersistent = false };
        if (current.Properties?.Items.TryGetValue(AuthenticationClaims.SignedInAtProperty, out var signedInAt) == true)
        {
            properties.Items[AuthenticationClaims.SignedInAtProperty] = signedInAt;
        }

        await signInManager.SignInWithClaimsAsync(
            user,
            properties,
            [new Claim(AuthenticationClaims.Amr, AuthenticationClaims.AmrMultiFactor)]);
    }
}
