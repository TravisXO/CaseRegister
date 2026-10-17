using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Notifications;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Slows down guessing without giving a guesser a way to shut a real person out.
///
///  - Wrong passwords are counted per account and per account and address. Five from one address lock that address out of that account for
///    15 minutes, then 1 hour, then 4 hours (each lockout within 24 hours is one step up the ladder). Another address is not affected, so
///    somebody guessing from far away cannot stop an admin signing in from their own desk.
///  - The whole account is locked (on the same ladder) only when wrong passwords came from three or more addresses: that is an attack
///    from many places, not one person mistyping.
///  - Wrong second-factor codes are counted apart. Ten in a row lock the authenticator until the person signs in with a recovery code.
///  - A full sign-in (password and second factor) resets the counters.
///  - Nobody's status ever changes because of any of this, and every lockout is written to the audit log and tells the person and the admins.
///
/// (A bot check is already asked for on every sign-in, which is stricter than asking after three wrong passwords, so there is no
/// separate "after three" step to add.)
/// </summary>
internal sealed class SignInThrottle(
    AppDbContext db,
    IAuditLog audit,
    TimeProvider time,
    IClientLocationAccessor location,
    NotificationDispatcher notifications,
    Microsoft.Extensions.Logging.ILogger<SignInThrottle> logger)
{
    public const int PasswordFailuresBeforeAddressLockout = 5;
    public const int AddressesBeforeAccountLockout = 3;
    public const int SecondFactorFailuresBeforeLock = 10;
    public const string UnknownAddress = "unknown";

    /// <summary>Lockouts and failures count towards the next step for this long.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>The ladder: the first lockout in 24 hours, the second, and every one after.</summary>
    public static readonly TimeSpan[] Ladder = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(4)];

    public static TimeSpan DurationFor(int level) => Ladder[Math.Min(Math.Max(level, 1), Ladder.Length) - 1];

    private static string Normalise(string? address) => string.IsNullOrWhiteSpace(address) ? UnknownAddress : address;

    // --- Is this address locked out of this account? ----------------------------------------------------------------

    /// <summary>When the lockout that applies to this address and account ends, or null if there is none.</summary>
    public async Task<DateTimeOffset?> LockedUntilAsync(Guid userId, string? address, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var pair = Normalise(address);

        return await db.SignInLockouts.AsNoTracking()
            .Where(lockout => lockout.UserId == userId && lockout.ClearedAt == null && lockout.LockedUntil > now && (lockout.IpAddress == null || lockout.IpAddress == pair))
            .MaxAsync(lockout => (DateTimeOffset?)lockout.LockedUntil, cancellationToken);
    }

    // --- Passwords --------------------------------------------------------------------------------------------------

    public async Task RecordPasswordFailureAsync(ApplicationUser user, string? address, CancellationToken cancellationToken)
    {
        var pair = Normalise(address);
        var now = time.GetUtcNow();

        db.SignInFailures.Add(new SignInFailure(user.Id, pair, FailureKind.Password));
        await db.SaveChangesAsync(cancellationToken);

        // One address, one account: five wrong passwords since this address was last locked out.
        var lastPairLockout = await LastLockoutAsync(user.Id, pair, cancellationToken);
        var streak = await db.SignInFailures.CountAsync(
            failure => failure.UserId == user.Id && failure.Kind == FailureKind.Password && failure.ClearedAt == null && failure.IpAddress == pair
                && (lastPairLockout == null || failure.CreatedAt > lastPairLockout),
            cancellationToken);

        if (streak >= PasswordFailuresBeforeAddressLockout)
        {
            await LockAsync(user, pair, "one address", cancellationToken);
        }

        // The whole account: wrong passwords from three or more addresses since the account was last locked as a whole.
        var lastAccountLockout = await LastLockoutAsync(user.Id, null, cancellationToken);
        var since = now - Window;
        var addresses = await db.SignInFailures.AsNoTracking()
            .Where(failure => failure.UserId == user.Id && failure.Kind == FailureKind.Password && failure.ClearedAt == null && failure.CreatedAt > since
                && (lastAccountLockout == null || failure.CreatedAt > lastAccountLockout))
            .Select(failure => failure.IpAddress)
            .Distinct()
            .CountAsync(cancellationToken);

        if (addresses >= AddressesBeforeAccountLockout && await LockedUntilForWholeAccountAsync(user.Id, cancellationToken) is null)
        {
            await LockAsync(user, null, $"wrong passwords from {addresses} addresses", cancellationToken);
        }
    }

    private async Task<DateTimeOffset?> LastLockoutAsync(Guid userId, string? pair, CancellationToken cancellationToken) =>
        await db.SignInLockouts.AsNoTracking()
            .Where(lockout => lockout.UserId == userId && lockout.ClearedAt == null && lockout.IpAddress == pair)
            .MaxAsync(lockout => (DateTimeOffset?)lockout.CreatedAt, cancellationToken);

    private async Task<DateTimeOffset?> LockedUntilForWholeAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        return await db.SignInLockouts.AsNoTracking()
            .Where(lockout => lockout.UserId == userId && lockout.ClearedAt == null && lockout.IpAddress == null && lockout.LockedUntil > now)
            .MaxAsync(lockout => (DateTimeOffset?)lockout.LockedUntil, cancellationToken);
    }

    private async Task LockAsync(ApplicationUser user, string? pair, string why, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var recent = await db.SignInLockouts.CountAsync(
            lockout => lockout.UserId == user.Id && lockout.ClearedAt == null && lockout.IpAddress == pair && lockout.CreatedAt > now - Window,
            cancellationToken);
        var level = recent + 1;
        var duration = DurationFor(level);

        var lockout = new SignInLockout(user.Id, pair, level, now + duration);
        db.SignInLockouts.Add(lockout);
        await db.SaveChangesAsync(cancellationToken);

        await audit.WriteStandaloneAsync(
            new AuditEntry(
                AuditAction.AccountLockedOut,
                user.Id,
                Subject: user.Id.ToString(),
                IpAddress: pair == UnknownAddress ? null : pair,
                Detail: $"{(pair is null ? "whole account" : "this address")} locked out for {(int)duration.TotalMinutes} minutes (step {level}): {why}. No status was changed.",
                TargetType: "User"),
            logger, cancellationToken);

        // Told after it is committed, to the person and to the admins, and never at the cost of the sign-in's own answer.
        await notifications.LockoutAsync(user.Id, lockout.Id, duration, location.Current, wholeAccount: pair is null, cancellationToken);
    }

    // --- Second factor ----------------------------------------------------------------------------------------------

    public async Task RecordSecondFactorFailureAsync(Guid userId, string? address, CancellationToken cancellationToken)
    {
        db.SignInFailures.Add(new SignInFailure(userId, Normalise(address), FailureKind.SecondFactor));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>True after ten wrong authenticator codes in a row. Only a recovery code unlocks it.</summary>
    public async Task<bool> SecondFactorLockedAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.SignInFailures.CountAsync(failure => failure.UserId == userId && failure.Kind == FailureKind.SecondFactor && failure.ClearedAt == null, cancellationToken)
            >= SecondFactorFailuresBeforeLock;

    /// <summary>A right code (or a recovery code) ends the run of wrong ones.</summary>
    public async Task ClearSecondFactorFailuresAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        await db.SignInFailures
            .Where(failure => failure.UserId == userId && failure.Kind == FailureKind.SecondFactor && failure.ClearedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(failure => failure.ClearedAt, now).SetProperty(failure => failure.UpdatedAt, now), cancellationToken);
    }

    // --- A full sign-in ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Password and second factor both passed: the counters start again. The account's lockout of the whole account ends, and so does this
    /// address's. A lockout of some other address stays until it runs out: it was not this person's.
    /// </summary>
    public async Task RecordFullSignInAsync(Guid userId, string? address, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var pair = Normalise(address);

        await db.SignInFailures
            .Where(failure => failure.UserId == userId && failure.ClearedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(failure => failure.ClearedAt, now).SetProperty(failure => failure.UpdatedAt, now), cancellationToken);

        await db.SignInLockouts
            .Where(lockout => lockout.UserId == userId && lockout.ClearedAt == null && (lockout.IpAddress == null || lockout.IpAddress == pair))
            .ExecuteUpdateAsync(set => set.SetProperty(lockout => lockout.ClearedAt, now).SetProperty(lockout => lockout.UpdatedAt, now), cancellationToken);
    }
}
