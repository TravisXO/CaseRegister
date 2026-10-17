using CaseRegister.Application.Accounts;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Notifications;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Approvals;

/// <summary>Things an operation hands back that must be shown once and kept nowhere: today, the setup token of an invite.</summary>
internal sealed class OperationOutput
{
    public string? SetupToken { get; set; }

    /// <summary>The one-time code a reset of somebody's two-factor made. Shown once to the admin who confirms, never stored in the clear.</summary>
    public string? EnrolmentCode { get; set; }

    /// <summary>What somebody should be told, recorded while the operation runs and turned into notifications only after the commit.</summary>
    public List<NotificationIntent> Intents { get; } = [];
}

/// <summary>Who an operation is run by, and what on.</summary>
/// <param name="ProposalReason">The reason the proposer gave, for operations that record why (a rejection).</param>
internal sealed class OperationContext(
    Guid actorId,
    Guid? targetUserId,
    Guid? targetAgencyId,
    string payload,
    string? targetEmail = null,
    string? proposalReason = null)
{
    public Guid ActorId { get; } = actorId;

    public Guid? TargetUserId { get; } = targetUserId;

    public Guid? TargetAgencyId { get; } = targetAgencyId;

    public string Payload { get; } = payload;

    public string? TargetEmail { get; } = targetEmail;

    public string? ProposalReason { get; } = proposalReason;

    /// <summary>Names this action in the notifications it causes: the proposal's id when there is one, so the same event is told once.</summary>
    public Guid EventId { get; init; } = Guid.CreateVersion7();

    public OperationOutput Output { get; init; } = new();
}

/// <summary>
/// What an operation would do right now. <see cref="Problems"/> is every rule that forbids it in the
/// current state; if there are none, <see cref="ApplyAsync"/> does it (inside the caller's transaction).
/// </summary>
internal sealed record OperationPlan(
    IReadOnlyList<ApprovalViolation> Problems,
    string Before,
    string After,
    AuditAction Effect,
    string? EffectSubject,
    Func<CancellationToken, Task> ApplyAsync)
{
    public static OperationPlan Refuse(params ApprovalViolation[] problems) =>
        new(problems, string.Empty, string.Empty, AuditAction.ActionRefused, null, _ => Task.CompletedTask);

    public static OperationPlan Refuse(string code, string message) => Refuse(new ApprovalViolation(code, message));
}

/// <summary>
/// One kind of admin action. There is one class per kind (Open/Closed): the approval engine and the
/// immediate actions both find the right one by <see cref="Type"/> and neither knows any of them.
/// </summary>
internal interface IAdminOperation
{
    PendingActionType Type { get; }

    Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken);
}

internal static class OperationProblems
{
    public static OperationPlan NoSuchTarget() => OperationPlan.Refuse("TargetNotFound", "The account or agency no longer exists.");

    public static OperationPlan WrongStatus(string what, string needed) =>
        OperationPlan.Refuse("TargetStatusChanged", $"{what} is no longer {needed}, so this no longer applies.");
}

/// <summary>Give or take away one role, through <see cref="IRoleAssignmentService"/>'s rules.</summary>
internal sealed class RoleChangeOperation(PendingActionType type, RoleAssignmentService roles, AppDbContext db) : IAdminOperation
{
    public PendingActionType Type { get; } = type;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var role = ActionPayloads.Role(context.Payload).Role;
        var change = Type == PendingActionType.AssignRole
            ? new RoleChange(context.TargetUserId!.Value, [role], [], ChangedByUserId: context.ActorId)
            : new RoleChange(context.TargetUserId!.Value, [], [role], ChangedByUserId: context.ActorId);

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == context.TargetUserId, cancellationToken);
        if (user is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (user.AccountStatus != AccountStatus.Approved)
        {
            return OperationProblems.WrongStatus("The account", "approved");
        }

        var outcome = await roles.PlanAsync(change, cancellationToken);
        if (outcome.Result is { Succeeded: false } refused)
        {
            return OperationPlan.Refuse([.. refused.Violations.Select(violation => new ApprovalViolation(violation.Code, violation.Message))]);
        }

        if (outcome.Plan is null)
        {
            // Already how it should be (the role is already held, or already gone). Nothing to do, and nothing to approve.
            return OperationPlan.Refuse("NothingToChange", Type == PendingActionType.AssignRole ? "The account already holds that role." : "The account does not hold that role.");
        }

        var plan = outcome.Plan;

        return new OperationPlan(
            [],
            $"roles: {string.Join(", ", plan.Before)}",
            $"roles: {string.Join(", ", plan.After)}",
            AuditAction.RolesChanged,
            plan.User.Id.ToString(),
            async token =>
            {
                await roles.ApplyAsync(plan, token);
                context.Output.Intents.Add(new RolesChangedIntent(context.EventId, plan.User.Id));
            });
    }
}

/// <summary>Marks a practising certificate verified. The second admin is the one recorded as having verified it.</summary>
internal sealed class VerifyCertificateOperation(AppDbContext db, TimeProvider time) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.VerifyPractisingCertificate;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var certificateId = ActionPayloads.Certificate(context.Payload).CertificateId;
        var certificate = await db.PractisingCertificates.SingleOrDefaultAsync(c => c.Id == certificateId, cancellationToken);

        if (certificate is null || certificate.UserId != context.TargetUserId)
        {
            return OperationPlan.Refuse("CertificateNotFound", "That certificate does not exist for this member.");
        }

        if (certificate.IsVerified)
        {
            return OperationPlan.Refuse("AlreadyVerified", "That certificate has already been verified.");
        }

        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == certificate.UserId, cancellationToken);
        if (owner is null || owner.AccountStatus != AccountStatus.Approved)
        {
            return OperationProblems.WrongStatus("The member's account", "approved");
        }

        return new OperationPlan(
            [],
            $"certificate {certificate.Number}: not verified",
            $"certificate {certificate.Number}: verified",
            AuditAction.PractisingCertificateVerified,
            certificate.Number.ToString(),
            token =>
            {
                certificate.Verify(context.ActorId, time.GetUtcNow());
                return db.SaveChangesAsync(token);
            });
    }
}

/// <summary>Suspends an account and ends its sessions. Refuses if it would leave fewer than two active admins.</summary>
internal sealed class SuspendUserOperation(UserManager<ApplicationUser> users, AppDbContext db) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.SuspendUser;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(context.TargetUserId!.Value.ToString());
        if (user is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (user.AccountStatus != AccountStatus.Approved)
        {
            return OperationProblems.WrongStatus("The account", "approved");
        }

        if (await AdminCensus.CheckMinimumAsync(db, user.Id, cancellationToken) is { } tooFew)
        {
            return OperationPlan.Refuse(tooFew);
        }

        return new OperationPlan(
            [],
            "status: Approved",
            "status: Suspended; all sessions ended",
            AuditAction.UserSuspended,
            user.Id.ToString(),
            async token =>
            {
                user.AccountStatus = AccountStatus.Suspended;
                SessionEnding.Ensure(await users.UpdateAsync(user));
                await SessionEnding.EndAllAsync(users, db, user, token);
            });
    }
}

/// <summary>Brings a suspended account back, with its lockout cleared. It still has to sign in with two-factor.</summary>
internal sealed class ReactivateUserOperation(UserManager<ApplicationUser> users, AppDbContext db, TimeProvider time) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.ReactivateUser;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(context.TargetUserId!.Value.ToString());
        if (user is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (user.AccountStatus != AccountStatus.Suspended)
        {
            return OperationProblems.WrongStatus("The account", "suspended");
        }

        return new OperationPlan(
            [],
            "status: Suspended",
            "status: Approved; lockout cleared",
            AuditAction.UserReactivated,
            user.Id.ToString(),
            async token =>
            {
                user.AccountStatus = AccountStatus.Approved;
                SessionEnding.Ensure(await users.UpdateAsync(user));

                // Any lockout left over from before is cleared too (kept as history, marked cleared).
                var now = time.GetUtcNow();
                await db.SignInLockouts.Where(lockout => lockout.UserId == user.Id && lockout.ClearedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(lockout => lockout.ClearedAt, now).SetProperty(lockout => lockout.UpdatedAt, now), token);
                await db.SignInFailures.Where(failure => failure.UserId == user.Id && failure.ClearedAt == null)
                    .ExecuteUpdateAsync(set => set.SetProperty(failure => failure.ClearedAt, now).SetProperty(failure => failure.UpdatedAt, now), token);
            });
    }
}

/// <summary>Signs a person out everywhere at once and leaves their account as it is.</summary>
internal sealed class EndSessionsOperation(UserManager<ApplicationUser> users, AppDbContext db) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.EndSessions;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(context.TargetUserId!.Value.ToString());
        if (user is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (user.AccountStatus != AccountStatus.Approved)
        {
            return OperationProblems.WrongStatus("The account", "approved");
        }

        var open = await db.Sessions.CountAsync(session => session.UserId == user.Id, cancellationToken);

        return new OperationPlan(
            [],
            $"open sessions: {open}",
            "open sessions: 0",
            AuditAction.SessionsEnded,
            user.Id.ToString(),
            token => SessionEnding.EndAllAsync(users, db, user, token));
    }
}

/// <summary>
/// Removes a person's authenticator and recovery codes, ends their sessions (and rotates their security stamp), and makes a one-time
/// enrolment code that, with their password, lets them enrol again. The old records are kept, marked removed: nothing is deleted.
/// </summary>
internal sealed class ResetTwoFactorOperation(UserManager<ApplicationUser> users, AppDbContext db, TimeProvider time) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.ResetTwoFactor;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(context.TargetUserId!.Value.ToString());
        if (user is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (user.AccountStatus != AccountStatus.Approved)
        {
            return OperationProblems.WrongStatus("The account", "approved");
        }

        var authenticator = await db.Authenticators.SingleOrDefaultAsync(record => record.UserId == user.Id, cancellationToken);
        var waitingForEnrolment = await db.EnrolmentCodes.AnyAsync(code => code.UserId == user.Id && code.CompletedAt == null && code.RevokedAt == null, cancellationToken);
        if (authenticator is null && !waitingForEnrolment)
        {
            return OperationPlan.Refuse("NothingToReset", "The account has no two-factor set up, so there is nothing to reset.");
        }

        var unusedCodes = await db.RecoveryCodes.CountAsync(code => code.UserId == user.Id && code.UsedAt == null && code.RevokedAt == null, cancellationToken);

        return new OperationPlan(
            [],
            $"two-factor: {(authenticator is null ? "removed, waiting for a new set-up" : authenticator.EnabledAt is null ? "being set up" : "on")}; unused recovery codes: {unusedCodes}",
            "two-factor: removed; recovery codes revoked; all sessions ended; one-time enrolment code made (72 hours, single use)",
            AuditAction.TwoFactorReset,
            user.Id.ToString(),
            async token =>
            {
                var now = time.GetUtcNow();
                authenticator?.RemovedAt = now;

                var codes = await db.RecoveryCodes
                    .Where(code => code.UserId == user.Id && code.UsedAt == null && code.RevokedAt == null)
                    .ToListAsync(token);
                foreach (var code in codes)
                {
                    code.RevokedAt = now;
                }

                user.TwoFactorEnabled = false;
                SessionEnding.Ensure(await users.UpdateAsync(user));
                await SessionEnding.EndAllAsync(users, db, user, token);

                // A newer code replaces any that was never used or finished. Only the hash is kept.
                var older = await db.EnrolmentCodes.Where(code => code.UserId == user.Id && code.CompletedAt == null && code.RevokedAt == null).ToListAsync(token);
                foreach (var code in older)
                {
                    code.RevokedAt = now;
                }

                var enrolmentCode = EnrolmentCodes.Generate();
                db.EnrolmentCodes.Add(new EnrolmentCodeRecord(user.Id, EnrolmentCodes.Hash(enrolmentCode), now + EnrolmentCodes.Lifetime));
                await db.SaveChangesAsync(token);

                context.Output.EnrolmentCode = enrolmentCode;
                context.Output.Intents.Add(new TwoFactorResetIntent(context.EventId, user.Id));
            });
    }
}

internal sealed class SuspendAgencyOperation(AppDbContext db) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.SuspendAgency;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var agency = await db.Agencies.SingleOrDefaultAsync(a => a.Id == context.TargetAgencyId, cancellationToken);
        if (agency is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (agency.Status != AgencyStatus.Active)
        {
            return OperationProblems.WrongStatus("The agency", "active");
        }

        return new OperationPlan(
            [],
            "agency: Active",
            "agency: Suspended; its members lose every agent capability",
            AuditAction.AgencySuspended,
            agency.Id.ToString(),
            async token =>
            {
                agency.Suspend();
                await db.SaveChangesAsync(token);
                context.Output.Intents.Add(new AgencyStatusChangedIntent(context.EventId, agency.Id));
            });
    }
}

internal sealed class ReactivateAgencyOperation(AppDbContext db) : IAdminOperation
{
    public PendingActionType Type => PendingActionType.ReactivateAgency;

    public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
    {
        var agency = await db.Agencies.SingleOrDefaultAsync(a => a.Id == context.TargetAgencyId, cancellationToken);
        if (agency is null)
        {
            return OperationProblems.NoSuchTarget();
        }

        if (agency.Status != AgencyStatus.Suspended)
        {
            return OperationProblems.WrongStatus("The agency", "suspended");
        }

        return new OperationPlan(
            [],
            "agency: Suspended",
            "agency: Active",
            AuditAction.AgencyReactivated,
            agency.Id.ToString(),
            async token =>
            {
                agency.Reactivate();
                await db.SaveChangesAsync(token);
                context.Output.Intents.Add(new AgencyStatusChangedIntent(context.EventId, agency.Id));
            });
    }
}

internal sealed class AgencyStatusReader(AppDbContext db) : IAgencyStatus
{
    public Task<bool> IsActiveAsync(Guid agencyId, CancellationToken cancellationToken = default) =>
        db.Agencies.AsNoTracking().AnyAsync(agency => agency.Id == agencyId && agency.Status == AgencyStatus.Active, cancellationToken);
}
