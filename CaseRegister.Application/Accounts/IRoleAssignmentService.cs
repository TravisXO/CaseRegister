using CaseRegister.Domain.Accounts;

namespace CaseRegister.Application.Accounts;

/// <param name="UserId">Whose roles change.</param>
/// <param name="Add">Roles to give.</param>
/// <param name="Remove">Roles to take away.</param>
/// <param name="ComplianceOfficerAgencyId">The agency a compliance officer role is for. Null means the user's own agency.</param>
/// <param name="ChangedByUserId">Who asked for it, for the audit log. Null for system changes.</param>
public sealed record RoleChange(
    Guid UserId,
    IReadOnlyCollection<Role> Add,
    IReadOnlyCollection<Role> Remove,
    Guid? ComplianceOfficerAgencyId = null,
    Guid? ChangedByUserId = null);

public sealed record RoleChangeResult(bool Succeeded, bool UserNotFound, IReadOnlyList<RoleRuleViolation> Violations)
{
    public static RoleChangeResult Done { get; } = new(true, false, []);

    public static RoleChangeResult NoSuchUser { get; } = new(false, true, []);

    public static RoleChangeResult Refused(IReadOnlyList<RoleRuleViolation> violations) => new(false, false, violations);
}

/// <summary>
/// The only way a role changes. It checks the separation-of-duties rules for the whole resulting set,
/// changes nothing if any is broken, and when a change is made it ends the user's sessions at once so
/// it takes effect immediately. There are no endpoints for this yet: approval by two admins comes next.
/// </summary>
public interface IRoleAssignmentService
{
    Task<RoleChangeResult> ChangeRolesAsync(RoleChange change, CancellationToken cancellationToken = default);
}
