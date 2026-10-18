using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Notifications;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Approvals;

internal sealed class BreakGlassService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    IEnumerable<IAdminOperation> operations,
    IAuditKeyring keyring,
    NotificationDispatcher notifications,
    IAuditLog audit,
    TimeProvider time) : IBreakGlassService
{
    /// <summary>The same person cannot be the subject of another break-glass action for this long.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(24);

    private readonly Dictionary<PendingActionType, IAdminOperation> _operations = operations.ToDictionary(operation => operation.Type);

    public async Task<string?> CheckAuditKeyAsync(CancellationToken cancellationToken = default)
    {
        var latest = await db.AuditLog.AsNoTracking().OrderByDescending(record => record.Sequence).Select(record => record.KeyId).FirstOrDefaultAsync(cancellationToken);

        return latest is null || string.Equals(latest, keyring.Current.Id, StringComparison.Ordinal)
            ? null
            : $"The audit log's latest entry was signed with key '{latest}', but this process would sign with '{keyring.Current.Id}'. That means the wrong keys are configured here, so nothing was written.";
    }

    public async Task<BreakGlassTarget?> FindAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByEmailAsync(EmailNormaliser.Normalise(email));
        if (user is null)
        {
            return null;
        }

        return new BreakGlassTarget(
            user.Id,
            user.FullName,
            user.Email!,
            await AdminCensus.HoldsAdminRoleAsync(db, user.Id, cancellationToken),
            user.AccountStatus == AccountStatus.Approved);
    }

    public Task<BreakGlassResult> ResetTwoFactorAsync(Guid userId, string reason, CancellationToken cancellationToken = default) =>
        RunAsync(PendingActionType.ResetTwoFactor, "reset-two-factor", userId, reason, cancellationToken);

    public Task<BreakGlassResult> SuspendAdminAsync(Guid userId, string reason, CancellationToken cancellationToken = default) =>
        RunAsync(PendingActionType.SuspendUser, "suspend-admin", userId, reason, cancellationToken);

    private async Task<BreakGlassResult> RunAsync(PendingActionType type, string command, Guid userId, string reason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var since = now - Cooldown;
        var id = userId.ToString();

        if (await db.AuditLog.AnyAsync(record => record.Action == nameof(AuditAction.BreakGlassUsed) && record.Subject == id && record.OccurredAt > since, cancellationToken))
        {
            return new BreakGlassResult(BreakGlassOutcome.LimitReached, "A break-glass action was already run on this person in the last 24 hours.");
        }

        BreakGlassResult? result = null;
        OperationContext context = null!;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteVerifiedAsync(db, async () =>
        {
            context = new OperationContext(Guid.Empty, userId, null, ActionPayloads.Empty) { BypassAdminFloor = type == PendingActionType.SuspendUser };
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (type == PendingActionType.SuspendUser)
            {
                // Taking an admin away: serialised with every other change to the set of admins.
                await AdminCensus.LockAsync(db, cancellationToken);
                if (!await AdminCensus.HoldsAdminRoleAsync(db, userId, cancellationToken))
                {
                    result = new BreakGlassResult(BreakGlassOutcome.NotApplicable, "That account is not an admin. Suspend it from the admin pages.");
                    return;
                }
            }

            if (await db.Users.AsNoTracking().AllAsync(user => user.Id != userId, cancellationToken))
            {
                result = new BreakGlassResult(BreakGlassOutcome.NotFound);
                return;
            }

            var plan = await _operations[type].PlanAsync(context, cancellationToken);
            if (plan.Problems.Count > 0)
            {
                result = new BreakGlassResult(BreakGlassOutcome.Refused, string.Join(' ', plan.Problems.Select(problem => problem.Message)));
                return;
            }

            await plan.ApplyAsync(cancellationToken);

            // Two entries: what was done to the account (as for any admin action), and that it was done from the command line, and why.
            await audit.WriteAsync(
                new AuditEntry(
                    plan.Effect,
                    ActorUserId: null,
                    Subject: plan.EffectSubject ?? id,
                    TargetType: "User",
                    Detail: AuditText.Fit($"break-glass; before: {plan.Before}; after: {plan.After}"),
                    Before: new { state = plan.Before },
                    After: new { state = plan.After }),
                cancellationToken);

            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.BreakGlassUsed,
                    ActorUserId: null,
                    Subject: id,
                    TargetType: "User",
                    Detail: AuditText.Fit($"{command} from the command line; reason: {reason.Trim()}")),
                cancellationToken);

            result = new BreakGlassResult(BreakGlassOutcome.Done, EnrolmentCode: context.Output.EnrolmentCode);
            await transaction.CommitAsync(cancellationToken);
        });

        if (result is { Outcome: BreakGlassOutcome.Done })
        {
            await notifications.BreakGlassAsync(userId, type == PendingActionType.ResetTwoFactor ? "two-factor sign-in was reset and all sessions ended" : "the account was suspended and all sessions ended", cancellationToken);
        }

        return result!;
    }
}
