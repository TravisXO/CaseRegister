using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Auditing;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// Remembers each lockout and suspends an account that is locked out too often. Three lockouts
/// within 24 hours mean someone is persistently guessing: the account is suspended and an admin has
/// to reactivate it (that comes in a later scope).
/// </summary>
internal sealed class LockoutTracker(
    AppDbContext context,
    UserManager<ApplicationUser> users,
    IAuditLog audit,
    Microsoft.Extensions.Logging.ILogger<LockoutTracker> logger,
    TimeProvider timeProvider)
{
    public const int SuspensionThreshold = 3;
    public static readonly TimeSpan SuspensionWindow = TimeSpan.FromHours(24);

    public async Task RecordLockoutAsync(ApplicationUser user, string? ipAddress, CancellationToken cancellationToken)
    {
        context.LockoutEvents.Add(new LockoutEvent(user.Id));
        await context.SaveChangesAsync(cancellationToken);
        await audit.WriteStandaloneAsync(new AuditEntry(AuditAction.AccountLockedOut, user.Id, IpAddress: ipAddress), logger, cancellationToken);

        var since = timeProvider.GetUtcNow() - SuspensionWindow;
        var recent = await context.LockoutEvents.CountAsync(e => e.UserId == user.Id && e.CreatedAt >= since, cancellationToken);

        if (recent >= SuspensionThreshold)
        {
            await SuspendAsync(user, ipAddress, cancellationToken);
        }
    }

    private async Task SuspendAsync(ApplicationUser user, string? ipAddress, CancellationToken cancellationToken)
    {
        user.AccountStatus = AccountStatus.Suspended;
        var updated = await users.UpdateAsync(user);

        if (!updated.Succeeded)
        {
            // Someone else changed the account at the same moment: reload and apply the suspension again.
            var fresh = await users.FindByIdAsync(user.Id.ToString());
            if (fresh is null)
            {
                return;
            }

            fresh.AccountStatus = AccountStatus.Suspended;
            await users.UpdateAsync(fresh);
            user = fresh;
        }

        // A new security stamp ends every session this account has open.
        await users.UpdateSecurityStampAsync(user);

        await audit.WriteStandaloneAsync(
            new AuditEntry(
                AuditAction.AccountSuspended,
                user.Id,
                IpAddress: ipAddress,
                Detail: $"{SuspensionThreshold} lockouts within {SuspensionWindow.TotalHours:0} hours"),
            logger, cancellationToken);
    }
}
