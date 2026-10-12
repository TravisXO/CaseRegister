using CaseRegister.Domain.Accounts;

namespace CaseRegister.Application.Authorization;

/// <summary>Everything a user can be allowed to do. Each one gets exactly one named policy, with the same name.</summary>
public enum Capability
{
    RunPreDealCheck,
    FileConductReport,
    ViewUpheldCases,
    DecideConductCases,
    RaiseInternalSuspicion,
    ReadFicReports,
    FileFicReports,
    ViewComplianceStatus,
    ViewFicTotals,
    ApproveAgents,
    ReadAuditLog,

    // Admin actions on accounts and agencies (maker-checker). Admins only; none involves an agent role.
    ViewPendingActions,
    ProposeAdminActions,
    ConfirmAdminActions,
    RestrictAccess,
    ViewApplications,
}

/// <summary>How far a role's capability reaches across records.</summary>
public enum AccessScope
{
    /// <summary>No particular record is involved (a check, a filing, a decision queue).</summary>
    Unscoped = 0,

    /// <summary>Only records the user owns.</summary>
    Own = 1,

    /// <summary>Only records of the user's own agency.</summary>
    OwnAgency = 2,

    /// <summary>Every record, in every agency.</summary>
    All = 3,
}

/// <summary>
/// The capability matrix: who may do what, and how far. This is the one place it is written down.
/// The authorisation policies, the resource checks and the tests of "can this role do that" are all
/// built from or compared against it, so a change here is a change everywhere, and the table test
/// fails until it is made deliberately.
/// </summary>
public static class PermissionMatrix
{
    private static readonly Dictionary<Capability, IReadOnlyDictionary<Role, AccessScope>> _matrix =
        new Dictionary<Capability, IReadOnlyDictionary<Role, AccessScope>>
        {
            [Capability.RunPreDealCheck] = Grant(
                (Role.RegisteredAgent, AccessScope.Unscoped),
                (Role.AgencyComplianceOfficer, AccessScope.Unscoped),
                (Role.CaseReviewer, AccessScope.Unscoped),
                (Role.Admin, AccessScope.Unscoped)),

            [Capability.FileConductReport] = Grant(
                (Role.RegisteredAgent, AccessScope.Unscoped),
                (Role.AgencyComplianceOfficer, AccessScope.Unscoped)),

            [Capability.ViewUpheldCases] = Grant(
                (Role.RegisteredAgent, AccessScope.Unscoped),
                (Role.AgencyComplianceOfficer, AccessScope.Unscoped),
                (Role.CaseReviewer, AccessScope.Unscoped),
                (Role.Admin, AccessScope.Unscoped)),

            [Capability.DecideConductCases] = Grant(
                (Role.CaseReviewer, AccessScope.Unscoped)),

            [Capability.RaiseInternalSuspicion] = Grant(
                (Role.RegisteredAgent, AccessScope.Unscoped),
                (Role.AgencyComplianceOfficer, AccessScope.Unscoped)),

            [Capability.ReadFicReports] = Grant(
                (Role.RegisteredAgent, AccessScope.Own),
                (Role.AgencyComplianceOfficer, AccessScope.OwnAgency)),

            [Capability.FileFicReports] = Grant(
                (Role.AgencyComplianceOfficer, AccessScope.Unscoped)),

            [Capability.ViewComplianceStatus] = Grant(
                (Role.AgencyComplianceOfficer, AccessScope.OwnAgency),
                (Role.Admin, AccessScope.All)),

            [Capability.ViewFicTotals] = Grant(
                (Role.AgencyComplianceOfficer, AccessScope.OwnAgency),
                (Role.Admin, AccessScope.All)),

            [Capability.ApproveAgents] = Grant(
                (Role.Admin, AccessScope.Unscoped)),

            [Capability.ReadAuditLog] = Grant(
                (Role.AgencyComplianceOfficer, AccessScope.OwnAgency),
                (Role.Admin, AccessScope.All)),

            [Capability.ViewPendingActions] = Grant((Role.Admin, AccessScope.Unscoped)),

            // Propose, and withdraw one's own proposal.
            [Capability.ProposeAdminActions] = Grant((Role.Admin, AccessScope.Unscoped)),

            // Confirm or reject someone else's proposal. Needs a fresh two-factor code on top.
            [Capability.ConfirmAdminActions] = Grant((Role.Admin, AccessScope.Unscoped)),

            // Suspend an account or an agency, or end sessions, at once.
            [Capability.RestrictAccess] = Grant((Role.Admin, AccessScope.Unscoped)),

            // The waiting applications from would-be members, with their flags.
            [Capability.ViewApplications] = Grant((Role.Admin, AccessScope.Unscoped)),
        };

    public static IEnumerable<Capability> Capabilities => Enum.GetValues<Capability>();

    /// <summary>The roles that hold a capability, each with how far it reaches.</summary>
    public static IReadOnlyDictionary<Role, AccessScope> RolesFor(Capability capability) => _matrix[capability];

    public static bool IsAllowed(Capability capability, IEnumerable<Role> roles) =>
        roles.Any(role => _matrix[capability].ContainsKey(role));

    /// <summary>The widest scope any of the user's roles gives for a capability, or null if none does.</summary>
    public static AccessScope? WidestScope(Capability capability, IEnumerable<Role> roles)
    {
        AccessScope? widest = null;

        foreach (var role in roles)
        {
            if (_matrix[capability].TryGetValue(role, out var scope) && (widest is null || scope > widest))
            {
                widest = scope;
            }
        }

        return widest;
    }

    /// <summary>
    /// Whether an agent role can use this capability. If so, the capability also needs a current,
    /// verified practising certificate: agents without one can do almost nothing.
    /// </summary>
    public static bool IncludesAgentRole(Capability capability) => _matrix[capability].Keys.Any(role => role.IsAgentRole());

    private static Dictionary<Role, AccessScope> Grant(params (Role Role, AccessScope Scope)[] grants) =>
        grants.ToDictionary(grant => grant.Role, grant => grant.Scope);
}
