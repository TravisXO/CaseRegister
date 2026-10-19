using System.Security.Claims;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CaseRegister.Infrastructure.Approvals;

/// <summary>Who the admins are. One definition of "active admin", used by every check of the two-admin minimum.</summary>
internal static class AdminCensus
{
    // Any constant will do; it only has to be the same everywhere the admin set is changed.
    private const long LockKey = 7_413_020_001;

    /// <summary>Approved accounts that hold the Admin role. A suspended admin is not active.</summary>
    public static IQueryable<Guid> ActiveAdminIds(AppDbContext db)
    {
        var adminRoleId = RoleSeed.IdOf(Role.Admin);

        return from userRole in db.UserRoles
               join user in db.Users on userRole.UserId equals user.Id
               where userRole.RoleId == adminRoleId && user.AccountStatus == AccountStatus.Approved
               select user.Id;
    }

    public static Task<int> CountActiveAsync(AppDbContext db, CancellationToken cancellationToken) =>
        ActiveAdminIds(db).CountAsync(cancellationToken);

    public static Task<bool> IsActiveAdminAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken) =>
        ActiveAdminIds(db).AnyAsync(id => id == userId, cancellationToken);

    /// <summary>Holds the Admin role, whatever the account's status. What decides whether an action on them needs two admins.</summary>
    public static Task<bool> HoldsAdminRoleAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var adminRoleId = RoleSeed.IdOf(Role.Admin);

        return db.UserRoles.AnyAsync(userRole => userRole.UserId == userId && userRole.RoleId == adminRoleId, cancellationToken);
    }

    /// <summary>
    /// Takes the lock that serialises changes to the set of admins, until the current transaction ends. Without
    /// it, two changes that are each fine alone could both be checked against the same count and together
    /// leave fewer than two.
    /// </summary>
    public static async Task LockAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);

        // And the admin rows themselves, so nothing that changes an admin (their status, their roles) can slip in between the count
        // and the change, from a code path that does not know about the advisory lock.
        var adminRoleId = RoleSeed.IdOf(Role.Admin);
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM users u WHERE u.id IN (SELECT ur.user_id FROM user_roles ur WHERE ur.role_id = {adminRoleId}) FOR UPDATE OF u",
            cancellationToken);
    }

    /// <summary>The refusal for a change that would leave too few active admins, or null if it is fine.</summary>
    public static async Task<ApprovalViolation?> CheckMinimumAsync(AppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsActiveAdminAsync(db, userId, cancellationToken))
        {
            return null; // not an active admin, so nothing is lost
        }

        var active = await CountActiveAsync(db, cancellationToken);

        return ApprovalRules.WouldLeaveTooFewAdmins(active, 1)
            ? new ApprovalViolation(
                ApprovalRules.MinimumAdmins,
                $"There must always be at least {ApprovalRules.MinimumActiveAdmins} active admins. This would leave {active - 1}.")
            : null;
    }
}

/// <summary>
/// One admin can take access away at once (suspend an account or an agency) ten times in any rolling 24 hours. Past that, it has to be
/// proposed and confirmed by a second admin. Ending sessions is exempt: in an incident it has to be possible at once, however often.
/// </summary>
internal static class ImmediateActionLimit
{
    public const int PerDay = 10;
    public const string Marker = "immediate;";
    public const string Code = "ImmediateLimit";

    public static async Task<bool> ReachedAsync(AppDbContext db, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now - TimeSpan.FromHours(24);
        var used = await db.AuditLog.CountAsync(
            record => record.ActorUserId == actorId && record.OccurredAt > since && record.Detail != null && record.Detail.StartsWith(Marker)
                && (record.Action == nameof(CaseRegister.Application.Auditing.AuditAction.UserSuspended) || record.Action == nameof(CaseRegister.Application.Auditing.AuditAction.AgencySuspended)),
            cancellationToken);

        return used >= PerDay;
    }
}

internal static class SessionEnding
{
    /// <summary>
    /// Signs a user out everywhere, at once: a new security stamp makes every session fail its next check,
    /// and deleting the session rows makes it immediate. (Sessions are the one thing that is hard-deleted, on purpose.)
    /// </summary>
    public static async Task EndAllAsync(UserManager<ApplicationUser> users, AppDbContext db, ApplicationUser user, CancellationToken cancellationToken)
    {
        Ensure(await users.UpdateSecurityStampAsync(user));
        await db.Sessions.Where(session => session.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// For somebody who has just changed something about their own security (their password, their recovery codes): their security stamp has
    /// been rotated, so this session is re-issued on the new stamp (keeping its public id and how it was signed in), and every other session
    /// of theirs is deleted at once rather than at its next stamp check. A session that does not carry its id (one from before this was
    /// kept) is still re-issued; the others then end at their next stamp check.
    /// </summary>
    public static async Task ReissueAndEndOthersAsync(SignInManager<ApplicationUser> signInManager, AppDbContext db, ApplicationUser user, CancellationToken cancellationToken)
    {
        var auth = await signInManager.Context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        List<Claim> carried = [];
        foreach (var type in new[] { AuthenticationClaims.Amr, AuthenticationClaims.SessionId, AuthenticationClaims.EnrolmentCode })
        {
            if (auth.Principal?.FindFirst(type) is { } claim)
            {
                carried.Add(new Claim(claim.Type, claim.Value));
            }
        }

        await signInManager.SignInWithClaimsAsync(user, auth.Properties, carried);

        if (Guid.TryParse(auth.Principal?.FindFirstValue(AuthenticationClaims.SessionId), out var current))
        {
            await db.Sessions.Where(session => session.UserId == user.Id && session.Id != current).ExecuteDeleteAsync(cancellationToken);
        }
    }

    public static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("An account change failed: " + string.Join(',', result.Errors.Select(error => error.Code)));
        }
    }
}

/// <summary>Short audit text. The audit column holds 500 characters, so the long free text goes last and is cut.</summary>
internal static class AuditText
{
    public const int Limit = 500;

    public static string Fit(string text) => text.Length <= Limit ? text : text[..(Limit - 1)] + "…";
}

internal static class Transactions
{
    /// <summary>
    /// Runs the work in the caller's transaction if there is one, or in a new one that commits at the end. Used so
    /// that a change and the audit entry about it always succeed or fail together. A lost commit acknowledgement does not run it twice
    /// (see <see cref="VerifiedExecution"/>).
    /// </summary>
    public static async Task RunAsync(AppDbContext db, Func<Task> work, CancellationToken cancellationToken, Func<CancellationToken, Task<bool>>? verifySucceeded = null)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteVerifiedAsync(
            db,
            async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await work();
                await transaction.CommitAsync(cancellationToken);
            },
            verifySucceeded);
    }
}

internal static class VerifiedExecution
{
    /// <summary>
    /// Runs a unit of work (which opens, and commits, its own transaction) under the retrying execution strategy, with the check that makes a retry safe.
    ///
    /// If the connection drops while the commit is on its way, the application cannot know whether the database committed or not. Retrying
    /// blindly would do everything twice. So before any retry the strategy asks <paramref name="verifySucceeded"/> whether the work is already there;
    /// the default looks for the last audit entry the attempt wrote (every state-changing transaction ends with one) and, when that exists, the
    /// work is treated as done and not run again. A unit of work that writes no audit entry cannot be told apart, and is repeated: it must be safe to repeat.
    /// </summary>
    public static async Task ExecuteVerifiedAsync(
        this IExecutionStrategy strategy,
        AppDbContext db,
        Func<Task> operation,
        Func<CancellationToken, Task<bool>>? verifySucceeded = null)
    {
        await strategy.ExecuteAsync(
            (db, operation, verifySucceeded),
            async (_, state, _) =>
            {
                state.db.LastAuditAppend = null;
                await state.operation();

                return true;
            },
            async (_, state, token) =>
            {
                var done = state.verifySucceeded is not null
                    ? await state.verifySucceeded(token)
                    : state.db.LastAuditAppend is { } last
                        && await state.db.AuditLog.AsNoTracking().AnyAsync(record => record.Sequence == last.Sequence && record.EntryHash == last.Hash, token);

                return new ExecutionResult<bool>(done, true);
            },
            CancellationToken.None);
    }
}
