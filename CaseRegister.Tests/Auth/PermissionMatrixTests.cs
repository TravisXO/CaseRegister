using CaseRegister.Application.Authorization;
using CaseRegister.Domain.Accounts;

namespace CaseRegister.Tests.Auth;

/// <summary>
/// The matrix, written out a second time as a plain table. If someone changes the real matrix, these
/// tests fail until this table is changed to match: a change to who may do what cannot slip through.
/// </summary>
public static class ExpectedMatrix
{
    // Every grant in the specification. A (capability, role) pair that is not listed is NOT allowed.
    public static readonly IReadOnlyDictionary<(Capability, Role), AccessScope> Grants = new Dictionary<(Capability, Role), AccessScope>
    {
        [(Capability.RunPreDealCheck, Role.RegisteredAgent)] = AccessScope.Unscoped,
        [(Capability.RunPreDealCheck, Role.AgencyComplianceOfficer)] = AccessScope.Unscoped,
        [(Capability.RunPreDealCheck, Role.CaseReviewer)] = AccessScope.Unscoped,
        [(Capability.RunPreDealCheck, Role.Admin)] = AccessScope.Unscoped,

        [(Capability.FileConductReport, Role.RegisteredAgent)] = AccessScope.Unscoped,
        [(Capability.FileConductReport, Role.AgencyComplianceOfficer)] = AccessScope.Unscoped,

        [(Capability.ViewUpheldCases, Role.RegisteredAgent)] = AccessScope.Unscoped,
        [(Capability.ViewUpheldCases, Role.AgencyComplianceOfficer)] = AccessScope.Unscoped,
        [(Capability.ViewUpheldCases, Role.CaseReviewer)] = AccessScope.Unscoped,
        [(Capability.ViewUpheldCases, Role.Admin)] = AccessScope.Unscoped,

        [(Capability.DecideConductCases, Role.CaseReviewer)] = AccessScope.Unscoped,

        [(Capability.RaiseInternalSuspicion, Role.RegisteredAgent)] = AccessScope.Unscoped,
        [(Capability.RaiseInternalSuspicion, Role.AgencyComplianceOfficer)] = AccessScope.Unscoped,

        [(Capability.ReadFicReports, Role.RegisteredAgent)] = AccessScope.Own,
        [(Capability.ReadFicReports, Role.AgencyComplianceOfficer)] = AccessScope.OwnAgency,

        [(Capability.FileFicReports, Role.AgencyComplianceOfficer)] = AccessScope.Unscoped,

        [(Capability.ViewComplianceStatus, Role.AgencyComplianceOfficer)] = AccessScope.OwnAgency,
        [(Capability.ViewComplianceStatus, Role.Admin)] = AccessScope.All,

        [(Capability.ViewFicTotals, Role.AgencyComplianceOfficer)] = AccessScope.OwnAgency,
        [(Capability.ViewFicTotals, Role.Admin)] = AccessScope.All,

        [(Capability.ApproveAgents, Role.Admin)] = AccessScope.Unscoped,

        [(Capability.ReadAuditLog, Role.AgencyComplianceOfficer)] = AccessScope.OwnAgency,
        [(Capability.ReadAuditLog, Role.Admin)] = AccessScope.All,

        // Admin actions on accounts and agencies: admins only.
        [(Capability.ViewPendingActions, Role.Admin)] = AccessScope.Unscoped,
        [(Capability.ProposeAdminActions, Role.Admin)] = AccessScope.Unscoped,
        [(Capability.ConfirmAdminActions, Role.Admin)] = AccessScope.Unscoped,
        [(Capability.RestrictAccess, Role.Admin)] = AccessScope.Unscoped,
        [(Capability.ViewApplications, Role.Admin)] = AccessScope.Unscoped,
        [(Capability.VerifyAuditLog, Role.Admin)] = AccessScope.Unscoped,

        // Who is told about lockouts and break-glass actions.
        [(Capability.ReceiveSecurityAlerts, Role.Admin)] = AccessScope.Unscoped,
    };

    public static bool Allows(Capability capability, Role role) => Grants.ContainsKey((capability, role));

    public static string[] PolicyNames { get; } =
    [
        "RunPreDealCheck", "FileConductReport", "ViewUpheldCases", "DecideConductCases", "RaiseInternalSuspicion",
        "ReadFicReports", "FileFicReports", "ViewComplianceStatus", "ViewFicTotals", "ApproveAgents", "ReadAuditLog",
        "ViewPendingActions", "ProposeAdminActions", "ConfirmAdminActions", "RestrictAccess", "ViewApplications", "VerifyAuditLog",
        "ReceiveSecurityAlerts",
    ];

    /// <summary>Every (capability, role) pair: 18 actions by 4 roles.</summary>
    public static TheoryData<Capability, Role> EveryCell()
    {
        var data = new TheoryData<Capability, Role>();
        foreach (var capability in Enum.GetValues<Capability>())
        {
            foreach (var role in Enum.GetValues<Role>())
            {
                data.Add(capability, role);
            }
        }

        return data;
    }
}

public class PermissionMatrixTests
{
    [Fact]
    public void There_is_one_capability_for_each_action_in_the_specification_with_the_names_given()
    {
        Assert.Equal(ExpectedMatrix.PolicyNames.Order(StringComparer.Ordinal), Enum.GetNames<Capability>().Order(StringComparer.Ordinal));
        Assert.Equal(18, PermissionMatrix.Capabilities.Count());
    }

    [Theory]
    [MemberData(nameof(ExpectedMatrix.EveryCell), MemberType = typeof(ExpectedMatrix))]
    public void Every_role_against_every_action_matches_the_specification(Capability capability, Role role)
    {
        var expected = ExpectedMatrix.Allows(capability, role);

        var actual = PermissionMatrix.RolesFor(capability).ContainsKey(role);

        Assert.Equal(expected, actual);
        Assert.Equal(expected, PermissionMatrix.IsAllowed(capability, [role]));
    }

    [Theory]
    [MemberData(nameof(ExpectedMatrix.EveryCell), MemberType = typeof(ExpectedMatrix))]
    public void Every_grant_reaches_exactly_as_far_as_the_specification_says(Capability capability, Role role)
    {
        if (!ExpectedMatrix.Grants.TryGetValue((capability, role), out var expectedScope))
        {
            Assert.Null(PermissionMatrix.WidestScope(capability, [role]));
            return;
        }

        Assert.Equal(expectedScope, PermissionMatrix.WidestScope(capability, [role]));
    }

    [Fact]
    public void The_matrix_holds_nothing_the_table_does_not_list()
    {
        var actualGrants = PermissionMatrix.Capabilities
            .SelectMany(capability => PermissionMatrix.RolesFor(capability).Select(grant => (capability, grant.Key)))
            .ToHashSet();

        Assert.Equal(ExpectedMatrix.Grants.Count, actualGrants.Count);
        Assert.Empty(actualGrants.Except(ExpectedMatrix.Grants.Keys));
    }

    [Fact]
    public void Admins_can_never_decide_cases_and_reviewers_can_never_approve_agents()
    {
        Assert.False(PermissionMatrix.IsAllowed(Capability.DecideConductCases, [Role.Admin]));
        Assert.False(PermissionMatrix.IsAllowed(Capability.ApproveAgents, [Role.CaseReviewer]));
        Assert.Equal([Role.CaseReviewer], PermissionMatrix.RolesFor(Capability.DecideConductCases).Keys);
    }

    [Fact]
    public void Admins_cannot_read_fic_reports_only_totals()
    {
        Assert.False(PermissionMatrix.IsAllowed(Capability.ReadFicReports, [Role.Admin]));
        Assert.True(PermissionMatrix.IsAllowed(Capability.ViewFicTotals, [Role.Admin]));
    }

    [Fact]
    public void Only_the_actions_an_agent_role_can_use_need_a_practising_certificate()
    {
        var needCertificate = PermissionMatrix.Capabilities.Where(PermissionMatrix.IncludesAgentRole).Order().ToList();

        Assert.Equal(
            [
                Capability.RunPreDealCheck, Capability.FileConductReport, Capability.ViewUpheldCases, Capability.RaiseInternalSuspicion,
                Capability.ReadFicReports, Capability.FileFicReports, Capability.ViewComplianceStatus, Capability.ViewFicTotals,
                Capability.ReadAuditLog,
            ],
            needCertificate);
        Assert.DoesNotContain(Capability.DecideConductCases, needCertificate);
        Assert.DoesNotContain(Capability.ApproveAgents, needCertificate);
    }

    [Fact]
    public void Several_roles_give_the_widest_reach_of_the_roles_held()
    {
        // Not a combination the role rules allow for one person, but the matrix must still answer sensibly.
        Assert.Equal(AccessScope.OwnAgency, PermissionMatrix.WidestScope(Capability.ReadFicReports, [Role.RegisteredAgent, Role.AgencyComplianceOfficer]));
        Assert.Null(PermissionMatrix.WidestScope(Capability.ReadFicReports, []));
    }
}

public class ResourceAccessTests
{
    private static readonly Guid _me = Guid.NewGuid();
    private static readonly Guid _someoneElse = Guid.NewGuid();
    private static readonly Guid _myAgency = Guid.NewGuid();
    private static readonly Guid _otherAgency = Guid.NewGuid();

    private sealed record Owned(Guid OwnerUserId) : IOwnedResource;

    private sealed record InAgency(Guid AgencyId) : IAgencyScopedResource;

    private sealed record Both(Guid OwnerUserId, Guid AgencyId) : IOwnedResource, IAgencyScopedResource;

    [Fact]
    public void Own_record_is_allowed_only_for_the_owner()
    {
        Assert.True(ResourceAccess.IsOwnRecord(_me, new Owned(_me)));
        Assert.False(ResourceAccess.IsOwnRecord(_me, new Owned(_someoneElse)));
        Assert.False(ResourceAccess.IsOwnRecord(_me, new InAgency(_myAgency))); // no owner at all
        Assert.False(ResourceAccess.IsOwnRecord(_me, new object()));
        Assert.False(ResourceAccess.IsOwnRecord(_me, null));
    }

    [Fact]
    public void Same_agency_is_allowed_only_within_the_users_agency()
    {
        Assert.True(ResourceAccess.IsSameAgency(_myAgency, new InAgency(_myAgency)));
        Assert.False(ResourceAccess.IsSameAgency(_myAgency, new InAgency(_otherAgency)));
        Assert.False(ResourceAccess.IsSameAgency(null, new InAgency(_myAgency))); // a user with no agency shares none
        Assert.False(ResourceAccess.IsSameAgency(_myAgency, new Owned(_me))); // the record belongs to no agency
        Assert.False(ResourceAccess.IsSameAgency(_myAgency, null));
    }

    [Fact]
    public void Same_agency_never_matches_an_empty_agency_id_against_a_user_with_none()
    {
        Assert.False(ResourceAccess.IsSameAgency(null, new InAgency(Guid.Empty)));
    }

    [Fact]
    public void A_record_in_my_agency_that_someone_else_owns_is_not_mine_but_is_my_agencys()
    {
        var record = new Both(_someoneElse, _myAgency);

        Assert.False(ResourceAccess.IsOwnRecord(_me, record));
        Assert.True(ResourceAccess.IsSameAgency(_myAgency, record));
    }

    [Theory]
    [InlineData(AccessScope.All, true, true, true)]
    [InlineData(AccessScope.OwnAgency, false, true, false)]
    [InlineData(AccessScope.Own, false, false, true)]
    public void A_scope_reaches_exactly_what_it_names(AccessScope scope, bool otherAgencyAndOwner, bool sameAgencyNotMine, bool mineInOtherAgency)
    {
        var foreign = new Both(_someoneElse, _otherAgency);
        var agencyMates = new Both(_someoneElse, _myAgency);
        var mineElsewhere = new Both(_me, _otherAgency);

        Assert.Equal(scope == AccessScope.All, ResourceAccess.Reaches(scope, _me, _myAgency, foreign));
        Assert.Equal(sameAgencyNotMine, ResourceAccess.Reaches(scope, _me, _myAgency, agencyMates));
        Assert.Equal(mineInOtherAgency, ResourceAccess.Reaches(scope, _me, _myAgency, mineElsewhere));
        Assert.True(otherAgencyAndOwner || !ResourceAccess.Reaches(scope, _me, _myAgency, foreign));
    }

    [Fact]
    public void An_unscoped_action_involves_no_record_so_any_resource_is_fine()
    {
        Assert.True(ResourceAccess.Reaches(AccessScope.Unscoped, _me, null, new object()));
    }
}
