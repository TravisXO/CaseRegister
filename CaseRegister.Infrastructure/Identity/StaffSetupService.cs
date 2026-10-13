using System.Collections.Concurrent;
using System.Security.Cryptography;
using CaseRegister.Application.Applications;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Invites;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Counts failed setup attempts per address. Past the limit within the hour every attempt from that address is
/// turned away, valid token or not, so a link cannot be guessed at.
/// </summary>
public sealed class SetupFailureTracker(TimeProvider time)
{
    public const int MaxFailuresPerHour = 10;
    private static readonly TimeSpan _window = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    public bool IsBlocked(string? address)
    {
        var failures = Trim(address ?? "unknown");

        lock (failures)
        {
            return failures.Count >= MaxFailuresPerHour;
        }
    }

    public void Record(string? address)
    {
        var failures = Trim(address ?? "unknown");

        lock (failures)
        {
            failures.Enqueue(time.GetUtcNow());
        }
    }

    public int Count(string? address)
    {
        var failures = Trim(address ?? "unknown");

        lock (failures)
        {
            return failures.Count;
        }
    }

    private Queue<DateTimeOffset> Trim(string address)
    {
        var failures = _failures.GetOrAdd(address, _ => new Queue<DateTimeOffset>());
        var cutoff = time.GetUtcNow() - _window;

        lock (failures)
        {
            while (failures.Count > 0 && failures.Peek() <= cutoff)
            {
                failures.Dequeue();
            }
        }

        return failures;
    }
}

/// <summary>
/// The last step of a staff invite: the invited person follows the link and sets a password and two-factor,
/// together. Nothing else can be done with the link. Every refusal looks the same to the caller and is counted.
/// </summary>
internal sealed class StaffSetupService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    IPasswordHasher<ApplicationUser> hasher,
    AuthenticatorKeyProtector keyProtector,
    SetupFailureTracker failures,
    IAuditLog audit,
    Microsoft.Extensions.Logging.ILogger<StaffSetupService> logger,
    TimeProvider time) : IStaffSetupService
{
    public const string Issuer = "CaseRegister";

    public async Task<StaffSetupResult> StartAsync(string token, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        if (failures.IsBlocked(remoteIpAddress))
        {
            return new StaffSetupResult(StaffSetupOutcome.RateLimited);
        }

        var found = await FindAsync(token, remoteIpAddress, "start", cancellationToken);
        if (found is null)
        {
            return new StaffSetupResult(StaffSetupOutcome.InvalidToken);
        }

        var (_, user) = found.Value;

        // A fresh key each time, replacing one that was never confirmed. The account has no password, so nothing can use it yet.
        var key = RandomNumberGenerator.GetBytes(Totp.KeyBytes);
        var record = await db.Authenticators.SingleOrDefaultAsync(r => r.UserId == user.Id, cancellationToken);
        if (record is null)
        {
            db.Authenticators.Add(new AuthenticatorRecord(user.Id, keyProtector.Protect(key)));
        }
        else
        {
            record.EncryptedKey = keyProtector.Protect(key);
            record.LastUsedStep = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        var sharedKey = Base32.Encode(key);

        return new StaffSetupResult(StaffSetupOutcome.Succeeded, Enrolment: new TwoFactorEnrolmentStart(Totp.BuildOtpAuthUri(Issuer, user.Email!, sharedKey), sharedKey));
    }

    public async Task<StaffSetupResult> CompleteAsync(string token, string password, string twoFactorCode, string? remoteIpAddress, CancellationToken cancellationToken = default)
    {
        if (failures.IsBlocked(remoteIpAddress))
        {
            return new StaffSetupResult(StaffSetupOutcome.RateLimited);
        }

        var found = await FindAsync(token, remoteIpAddress, "complete", cancellationToken);
        if (found is null)
        {
            return new StaffSetupResult(StaffSetupOutcome.InvalidToken);
        }

        var (setupToken, user) = found.Value;

        // The normal password rules. A password that fails them leaves the link usable.
        var problems = await PasswordChecks.ProblemsAsync(users, user, password);
        if (problems.Count > 0)
        {
            return new StaffSetupResult(StaffSetupOutcome.InvalidPassword, problems);
        }

        var record = await db.Authenticators.SingleOrDefaultAsync(r => r.UserId == user.Id && r.EnabledAt == null, cancellationToken);
        var now = time.GetUtcNow();
        if (record is null)
        {
            // Another request may have finished the setup a moment ago, which also used up the enrolment it was waiting on.
            // Whether the link is still good is settled first: if it is not, the answer is the same as for any spent link.
            var current = await db.StaffSetupTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == setupToken.Id, cancellationToken);

            return current is null || !current.IsUsable(now)
                ? new StaffSetupResult(StaffSetupOutcome.InvalidToken)
                : new StaffSetupResult(StaffSetupOutcome.NoEnrolmentStarted);
        }

        var typed = new string([.. twoFactorCode.Where(character => !char.IsWhiteSpace(character))]);
        if (!Totp.TryMatch(keyProtector.Unprotect(record.EncryptedKey), typed, Totp.CurrentStep(now), null, out var step))
        {
            failures.Record(remoteIpAddress);
            await LogFailureAsync(user.Id, "wrong code", remoteIpAddress, cancellationToken);

            return new StaffSetupResult(StaffSetupOutcome.InvalidCode);
        }

        IReadOnlyList<string> recoveryCodes = [];
        var spent = false;
        var strategy = db.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                spent = false;
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

                // The token is spent first. If another request spent it at the same moment, this save fails and nothing else happens.
                var fresh = await db.StaffSetupTokens.SingleAsync(t => t.Id == setupToken.Id, cancellationToken);
                if (!fresh.IsUsable(now))
                {
                    return;
                }

                fresh.Spend(now);
                await db.SaveChangesAsync(cancellationToken);

                var person = (await users.FindByIdAsync(user.Id.ToString()))!;
                var authenticator = await db.Authenticators.SingleAsync(r => r.UserId == user.Id && r.EnabledAt == null, cancellationToken);

                // Together, in one transaction: the password, the second factor, and the account becoming usable.
                person.PasswordHash = hasher.HashPassword(person, password);
                person.TwoFactorEnabled = true;
                person.AccountStatus = AccountStatus.Approved;
                SessionEnding.Ensure(await users.UpdateAsync(person));
                SessionEnding.Ensure(await users.UpdateSecurityStampAsync(person));

                authenticator.EnabledAt = now;
                authenticator.LastUsedStep = step;

                var codes = RecoveryCodes.Generate();
                foreach (var code in codes)
                {
                    db.RecoveryCodes.Add(new RecoveryCodeRecord(person.Id, RecoveryCodes.Hash(code)));
                }

                await db.SaveChangesAsync(cancellationToken);
                await audit.WriteAsync(
                    new AuditEntry(AuditAction.StaffSetupCompleted, person.Id, Subject: person.Id.ToString(), IpAddress: remoteIpAddress, Detail: "password set and two-factor enrolled; the account is now Approved"),
                    cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                recoveryCodes = codes;
                spent = true;
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            spent = false; // someone else used the link at the same moment
            db.ChangeTracker.Clear(); // forget the half-finished attempt before anything else is saved
        }

        if (!spent)
        {
            failures.Record(remoteIpAddress);
            await LogFailureAsync(user.Id, "already used", remoteIpAddress, cancellationToken);

            return new StaffSetupResult(StaffSetupOutcome.InvalidToken);
        }

        return new StaffSetupResult(StaffSetupOutcome.Succeeded, RecoveryCodes: recoveryCodes);
    }

    /// <summary>The usable token and its account, or null. A refusal is counted and logged, and says nothing about why.</summary>
    private async Task<(StaffSetupToken Token, ApplicationUser User)?> FindAsync(string token, string? remoteIpAddress, string step, CancellationToken cancellationToken)
    {
        var hash = StaffSetupToken.HashOf(token);
        var stored = await db.StaffSetupTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        var reason = stored is null ? "unknown token" : stored.Problem(time.GetUtcNow());
        ApplicationUser? user = null;

        if (stored is not null && reason is null)
        {
            user = await users.FindByIdAsync(stored.UserId.ToString());
            if (user is not { AccountStatus: AccountStatus.Pending, PasswordHash: null })
            {
                reason = "account is not waiting for setup";
            }
        }

        if (reason is not null || stored is null || user is null)
        {
            failures.Record(remoteIpAddress);
            await LogFailureAsync(stored?.UserId, $"{step}: {reason}", remoteIpAddress, cancellationToken);

            return null;
        }

        return (stored, user);
    }

    private Task LogFailureAsync(Guid? userId, string reason, string? remoteIpAddress, CancellationToken cancellationToken) =>
        audit.WriteStandaloneAsync(
            new AuditEntry(AuditAction.StaffSetupFailed, userId, Subject: userId?.ToString(), IpAddress: remoteIpAddress, Detail: AuditText.Fit($"setup refused: {reason}; failures from this address this hour: {failures.Count(remoteIpAddress)}")),
            logger, cancellationToken);
}
