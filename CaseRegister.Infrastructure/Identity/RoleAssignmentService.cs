using System.Security.Claims;
using CaseRegister.Application.Accounts;
using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Identity;

internal sealed class RoleAssignmentService(
    UserManager<ApplicationUser> users,
    AppDbContext db,
    IAuditLog audit) : IRoleAssignmentService
{
    public async Task<RoleChangeResult> ChangeRolesAsync(RoleChange change, CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(change.UserId.ToString());
        if (user is null)
        {
            return RoleChangeResult.NoSuchUser;
        }

        var current = (await users.GetRolesAsync(user)).Select(Enum.Parse<Role>).ToHashSet();

        // What the user would hold afterwards. The rules are checked on that whole set, not on the change,
        // so removing a role can break them too (taking away RegisteredAgent from a compliance officer).
        var proposed = current.Except(change.Remove).Union(change.Add).ToHashSet();

        var violations = RoleAssignmentRules.Validate(proposed, user.AgencyId, change.ComplianceOfficerAgencyId);
        if (violations.Count > 0)
        {
            return RoleChangeResult.Refused(violations);
        }

        var toAdd = proposed.Except(current).Select(role => role.ToString()).Order(StringComparer.Ordinal).ToList();
        var toRemove = current.Except(proposed).Select(role => role.ToString()).Order(StringComparer.Ordinal).ToList();
        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return RoleChangeResult.Done; // nothing to change: no new stamp, and nobody is signed out for nothing
        }

        // All or nothing: the roles, the new security stamp and ending the sessions succeed together or not at all.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            Ensure(await users.AddToRolesAsync(user, toAdd));
            Ensure(await users.RemoveFromRolesAsync(user, toRemove));

            // A new stamp makes every session fail its next check; deleting the sessions makes that immediate.
            Ensure(await users.UpdateSecurityStampAsync(user));
            await db.Sessions.Where(session => session.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        });

        await audit.WriteAsync(
            new AuditEntry(
                AuditAction.RolesChanged,
                change.ChangedByUserId,
                Subject: user.Id.ToString(),
                Detail: $"added: {string.Join(',', toAdd)}; removed: {string.Join(',', toRemove)}"),
            cancellationToken);

        return RoleChangeResult.Done;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("A role change failed: " + string.Join(',', result.Errors.Select(error => error.Code)));
        }
    }
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
