using CaseRegister.Application.Accounts;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auditing;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Approvals;

/// <summary>
/// The actions that take access away, done at once by one admin with a reason. They use the same
/// operations as the approval process, so "suspend" means one thing however it is reached. If the target
/// is an admin the rules say it needs two, and this refuses.
/// </summary>
internal sealed class AccountAdministrationService(
    AppDbContext db,
    IEnumerable<IAdminOperation> operations,
    IAuditLog audit) : IAccountAdministrationService
{
    private readonly Dictionary<PendingActionType, IAdminOperation> _operations = operations.ToDictionary(operation => operation.Type);

    public Task<AdminActionResult> SuspendUserAsync(Guid actorId, Guid userId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default) =>
        RunAsync(PendingActionType.SuspendUser, actorId, userId, null, reason, remoteIpAddress, cancellationToken);

    public Task<AdminActionResult> EndSessionsAsync(Guid actorId, Guid userId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default) =>
        RunAsync(PendingActionType.EndSessions, actorId, userId, null, reason, remoteIpAddress, cancellationToken);

    public Task<AdminActionResult> SuspendAgencyAsync(Guid actorId, Guid agencyId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default) =>
        RunAsync(PendingActionType.SuspendAgency, actorId, null, agencyId, reason, remoteIpAddress, cancellationToken);

    private async Task<AdminActionResult> RunAsync(
        PendingActionType type,
        Guid actorId,
        Guid? userId,
        Guid? agencyId,
        string reason,
        string? remoteIpAddress,
        CancellationToken cancellationToken)
    {
        if (!ApprovalRules.ReducesAccess(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), "Only actions that take access away can be done at once.");
        }

        if (!await AdminCensus.IsActiveAdminAsync(db, actorId, cancellationToken))
        {
            return new AdminActionResult(AdminActionOutcome.NotAnAdmin);
        }

        if (ApprovalRules.CheckReason(reason) is { } badReason)
        {
            return new AdminActionResult(AdminActionOutcome.Invalid, [badReason]);
        }

        var actorAgencyId = await db.Users.AsNoTracking().Where(user => user.Id == actorId).Select(user => user.AgencyId).SingleAsync(cancellationToken);
        var self = ApprovalRules.CheckProposal(actorId, actorAgencyId, userId, agencyId);
        if (self.Count > 0)
        {
            return new AdminActionResult(AdminActionOutcome.NotAllowed, self);
        }

        var exists = userId is { } user
            ? await db.Users.AnyAsync(u => u.Id == user, cancellationToken)
            : await db.Agencies.AnyAsync(a => a.Id == agencyId, cancellationToken);
        if (!exists)
        {
            return new AdminActionResult(AdminActionOutcome.NotFound);
        }

        // Nobody removes a peer on their own: against an admin this needs two.
        var targetIsAdmin = userId is { } id && await AdminCensus.HoldsAdminRoleAsync(db, id, cancellationToken);
        if (ApprovalRules.RequiresTwoAdmins(type, targetIsAdmin))
        {
            return new AdminActionResult(
                AdminActionOutcome.NeedsTwoAdmins,
                [new ApprovalViolation("NeedsTwoAdmins", "The target is an admin, so this needs a second admin to confirm it. Propose it instead.")]);
        }

        AdminActionResult? result = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (ApprovalRules.CanReduceAdmins(type))
            {
                await AdminCensus.LockAsync(db, cancellationToken);
            }

            var plan = await _operations[type].PlanAsync(new OperationContext(actorId, userId, agencyId, ActionPayloads.Empty), cancellationToken);
            if (plan.Problems.Count > 0)
            {
                result = new AdminActionResult(AdminActionOutcome.Refused, plan.Problems);
                return;
            }

            await plan.ApplyAsync(cancellationToken);

            await audit.WriteAsync(
                new AuditEntry(
                    plan.Effect,
                    actorId,
                    Subject: plan.EffectSubject ?? (userId ?? agencyId)!.Value.ToString(),
                    IpAddress: remoteIpAddress,
                    Detail: AuditText.Fit($"immediate; before: {plan.Before}; after: {plan.After}; reason: {reason.Trim()}"),
                    Before: new { state = plan.Before },
                    After: new { state = plan.After }),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            result = new AdminActionResult(AdminActionOutcome.Done);
        });

        return result!;
    }
}

/// <summary>Creates the first admins. See <see cref="IAdminSeeder"/>.</summary>
internal sealed class AdminSeeder(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    RoleAssignmentService roles,
    IAuditLog audit) : IAdminSeeder
{
    public Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default) =>
        AdminCensus.CountActiveAsync(db, cancellationToken);

    public async Task<IReadOnlyList<string>> CheckPasswordAsync(string email, string fullName, string password, CancellationToken cancellationToken = default)
    {
        var person = ApplicationUser.CreateStaff(email, fullName, AccountStatus.Approved);
        List<string> problems = [];

        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, person, password);
            problems.AddRange(result.Errors.Select(error => error.Description));
        }

        return problems;
    }

    public async Task<SeedAdminResult> SeedAsync(SeedAdminRequest request, CancellationToken cancellationToken = default)
    {
        SeedAdminResult? result = null;
        var activeBefore = 0;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Two runs at the same time must not both find "fewer than two" and each add one.
            await AdminCensus.LockAsync(db, cancellationToken);

            activeBefore = await AdminCensus.CountActiveAsync(db, cancellationToken);
            if (activeBefore >= ApprovalRules.MinimumActiveAdmins)
            {
                result = new SeedAdminResult(SeedAdminOutcome.EnoughAdmins, []);
                return;
            }

            // Association staff: no membership number, no agency. Approved at once, because the approval
            // process needs two admins before it can approve anyone. No authenticator: they must enrol at first sign-in.
            var user = ApplicationUser.CreateStaff(request.Email, request.FullName, AccountStatus.Approved);

            var created = await users.CreateAsync(user, request.Password); // the same password rules as everyone
            if (!created.Succeeded)
            {
                result = new SeedAdminResult(SeedAdminOutcome.Invalid, [.. created.Errors.Select(error => error.Description)]);
                return;
            }

            var plan = await roles.PlanAsync(new RoleChange(user.Id, [Role.Admin], []), cancellationToken);
            if (plan.Plan is null)
            {
                throw new InvalidOperationException("The new admin could not be given the Admin role.");
            }

            await roles.ApplyAsync(plan.Plan, cancellationToken);
            // The entry is written inside the transaction that creates the admin.
            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.AdminSeeded,
                    ActorUserId: null,
                    Subject: user.Id.ToString(),
                    TargetType: "User",
                    Detail: $"created an admin from the command line; active admins before: {activeBefore}; two-factor enrolment required at first sign-in"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            result = new SeedAdminResult(SeedAdminOutcome.Created, [], user.Id);
        });

        return result!;
    }
}
