using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
    /// that a change and the audit entry about it always succeed or fail together.
    /// </summary>
    public static async Task RunAsync(AppDbContext db, Func<Task> work, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await work();
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
