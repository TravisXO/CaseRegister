namespace CaseRegister.Domain.Accounts;

/// <param name="Code">A stable name for the rule, for tests and logs.</param>
/// <param name="Message">A plain-language reason, safe to show.</param>
public sealed record RoleRuleViolation(string Code, string Message);

/// <summary>
/// Separation of duties for roles. These are the rules; the service that changes roles asks them
/// before it changes anything, and nothing else changes a role.
/// </summary>
public static class RoleAssignmentRules
{
    public const string AgentRoleNeedsAgency = nameof(AgentRoleNeedsAgency);
    public const string StaffMustHaveNoAgency = nameof(StaffMustHaveNoAgency);
    public const string ComplianceOfficerMustBeRegisteredAgent = nameof(ComplianceOfficerMustBeRegisteredAgent);
    public const string ComplianceOfficerMustBeOfOwnAgency = nameof(ComplianceOfficerMustBeOfOwnAgency);
    public const string StaffCannotHoldAgentRoles = nameof(StaffCannotHoldAgentRoles);
    public const string AdminCannotBeCaseReviewer = nameof(AdminCannotBeCaseReviewer);

    /// <summary>Checks a whole set of roles for one user. An empty list means the combination is allowed.</summary>
    /// <param name="roles">The roles the user would hold. No roles at all is fine (a member waiting for approval).</param>
    /// <param name="userAgencyId">The one agency the user belongs to, or null for association staff.</param>
    /// <param name="complianceOfficerAgencyId">
    /// The agency the compliance officer role is being given for. Null means "the user's own agency".
    /// </param>
    public static IReadOnlyList<RoleRuleViolation> Validate(
        IReadOnlySet<Role> roles,
        Guid? userAgencyId,
        Guid? complianceOfficerAgencyId = null)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var violations = new List<RoleRuleViolation>();
        var hasAgentRole = roles.Any(role => role.IsAgentRole());
        var hasStaffRole = roles.Any(role => role.IsStaffRole());

        // Admin and CaseReviewer can't also be agents: staff do not act inside an agency.
        if (hasAgentRole && hasStaffRole)
        {
            violations.Add(new RoleRuleViolation(
                StaffCannotHoldAgentRoles,
                "Admins and case reviewers cannot also be registered agents or compliance officers."));
        }

        // Nobody can be both: an admin must never be able to decide a case.
        if (roles.Contains(Role.Admin) && roles.Contains(Role.CaseReviewer))
        {
            violations.Add(new RoleRuleViolation(
                AdminCannotBeCaseReviewer,
                "Nobody can be both an admin and a case reviewer: admins must never decide cases."));
        }

        // A compliance officer is a registered agent first.
        if (roles.Contains(Role.AgencyComplianceOfficer) && !roles.Contains(Role.RegisteredAgent))
        {
            violations.Add(new RoleRuleViolation(
                ComplianceOfficerMustBeRegisteredAgent,
                "A compliance officer must also be a registered agent."));
        }

        // ...and only of the agency they belong to.
        if (roles.Contains(Role.AgencyComplianceOfficer)
            && complianceOfficerAgencyId is not null
            && complianceOfficerAgencyId != userAgencyId)
        {
            violations.Add(new RoleRuleViolation(
                ComplianceOfficerMustBeOfOwnAgency,
                "A compliance officer must be a registered agent of the same agency."));
        }

        // Every agent belongs to exactly one agency; association staff belong to none.
        if (hasAgentRole && userAgencyId is null)
        {
            violations.Add(new RoleRuleViolation(AgentRoleNeedsAgency, "Agent roles need the user to belong to an agency."));
        }

        if (hasStaffRole && userAgencyId is not null)
        {
            violations.Add(new RoleRuleViolation(StaffMustHaveNoAgency, "Association staff do not belong to an agency."));
        }

        return violations;
    }
}
