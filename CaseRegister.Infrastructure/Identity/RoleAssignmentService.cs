using System.Security.Claims;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class RoleAssignmentService(
    UserManager<ApplicationUser> users,
    AppDbContext db,
    IAuditLog audit) : IRoleAssignmentService
{
    /// <summary>What a role change would do, worked out from the current state without changing anything.</summary>
    internal sealed record RolePlan(ApplicationUser User, IReadOnlyList<Role> Before, IReadOnlyList<Role> After, IReadOnlyList<string> ToAdd, IReadOnlyList<string> ToRemove);

    /// <summary>Either a refusal (or "nothing to change"), or a plan to apply.</summary>
    internal sealed record PlanOutcome(RoleChangeResult? Result, RolePlan? Plan);

    public async Task<RoleChangeResult> ChangeRolesAsync(RoleChange change, CancellationToken cancellationToken = default)
    {
        RoleChangeResult? result = null;

        // All or nothing: the roles, the new security stamp and ending the sessions succeed together or not at all.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Whoever changes the set of admins goes one at a time, so the minimum is checked against the truth.
            await AdminCensus.LockAsync(db, cancellationToken);

            var outcome = await PlanAsync(change, cancellationToken);
            if (outcome.Plan is null)
            {
                result = outcome.Result;
                return;
            }

            await ApplyAsync(outcome.Plan, cancellationToken);

            // The entry is written in the same transaction as the change: neither exists without the other.
            await audit.WriteAsync(AuditEntryFor(outcome.Plan, change.ChangedByUserId, approvalNote: null), cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            result = RoleChangeResult.Done;
        });

        return result!;
    }

    /// <summary>
    /// Checks a change against the rules on the whole resulting set, and the two-admin minimum, and says what
    /// it would do. Changes nothing. The approval process uses this to check before and again at confirmation.
    /// </summary>
    internal async Task<PlanOutcome> PlanAsync(RoleChange change, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(change.UserId.ToString());
        if (user is null)
        {
            return new PlanOutcome(RoleChangeResult.NoSuchUser, null);
        }

        var current = (await users.GetRolesAsync(user)).Select(Enum.Parse<Role>).ToHashSet();

        // What the user would hold afterwards. The rules are checked on that whole set, not on the change,
        // so removing a role can break them too (taking away RegisteredAgent from a compliance officer).
        var proposed = current.Except(change.Remove).Union(change.Add).ToHashSet();

        List<RoleRuleViolation> violations = [.. RoleAssignmentRules.Validate(proposed, user.AgencyId, change.ComplianceOfficerAgencyId)];

        // Taking the Admin role away from an active admin must not leave fewer than two.
        if (current.Contains(Role.Admin) && !proposed.Contains(Role.Admin)
            && await AdminCensus.CheckMinimumAsync(db, user.Id, cancellationToken) is { } tooFew)
        {
            violations.Add(new RoleRuleViolation(tooFew.Code, tooFew.Message));
        }

        if (violations.Count > 0)
        {
            return new PlanOutcome(RoleChangeResult.Refused(violations), null);
        }

        var toAdd = proposed.Except(current).Select(role => role.ToString()).Order(StringComparer.Ordinal).ToList();
        var toRemove = current.Except(proposed).Select(role => role.ToString()).Order(StringComparer.Ordinal).ToList();
        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return new PlanOutcome(RoleChangeResult.Done, null); // nothing to change: no new stamp, and nobody is signed out for nothing
        }

        return new PlanOutcome(null, new RolePlan(user, [.. current.Order()], [.. proposed.Order()], toAdd, toRemove));
    }

    /// <summary>Makes the change. Must run inside the caller's transaction.</summary>
    internal async Task ApplyAsync(RolePlan plan, CancellationToken cancellationToken)
    {
        SessionEnding.Ensure(await users.AddToRolesAsync(plan.User, plan.ToAdd));
        SessionEnding.Ensure(await users.RemoveFromRolesAsync(plan.User, plan.ToRemove));

        // A new stamp makes every session fail its next check; deleting the sessions makes that immediate.
        await SessionEnding.EndAllAsync(users, db, plan.User, cancellationToken);
    }

    internal static AuditEntry AuditEntryFor(RolePlan plan, Guid? changedBy, string? approvalNote) =>
        new(
            AuditAction.RolesChanged,
            changedBy,
            Subject: plan.User.Id.ToString(),
            TargetType: "User",
            Before: new { roles = plan.Before.Select(role => role.ToString()) },
            After: new { roles = plan.After.Select(role => role.ToString()) },
            Detail: AuditText.Fit(
                $"added: {string.Join(',', plan.ToAdd)}; removed: {string.Join(',', plan.ToRemove)}; "
                + $"before: {string.Join(',', plan.Before)}; after: {string.Join(',', plan.After)}"
                + (approvalNote is null ? string.Empty : $"; {approvalNote}")));
}

/// <summary>Adds the user's agency to the session, next to the role claims Identity already adds.</summary>
internal sealed class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    Microsoft.Extensions.Options.IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (user.AgencyId is { } agencyId)
        {
            identity.AddClaim(new Claim(AuthenticationClaims.AgencyId, agencyId.ToString()));
        }

        return identity;
    }
}
