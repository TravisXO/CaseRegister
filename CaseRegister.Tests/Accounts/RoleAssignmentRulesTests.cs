using CaseRegister.Domain.Accounts;

namespace CaseRegister.Tests.Accounts;

public class RoleAssignmentRulesTests
{
    private static readonly Guid _agency = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _otherAgency = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static HashSet<Role> Set(params Role[] roles) => [.. roles];

    private static IEnumerable<string> Codes(IReadOnlySet<Role> roles, Guid? agency, Guid? officerOf = null) =>
        RoleAssignmentRules.Validate(roles, agency, officerOf).Select(violation => violation.Code);

    // --- Every combination, against a table written independently of the rules --------------------

    // The ONLY combinations that are allowed. Anything not listed here must be refused.
    private static readonly (Role[] Roles, bool HasAgency)[] _allowed =
    [
        ([], true), // a member waiting for approval
        ([], false), // an account with no role yet
        ([Role.RegisteredAgent], true),
        ([Role.RegisteredAgent, Role.AgencyComplianceOfficer], true),
        ([Role.CaseReviewer], false),
        ([Role.Admin], false),
    ];

    public static TheoryData<int, bool, bool> EveryCombination()
    {
        var all = Enum.GetValues<Role>();
        var data = new TheoryData<int, bool, bool>();

        // Each of the 16 subsets of the four roles, with and without an agency: 32 cases.
        for (var mask = 0; mask < 1 << all.Length; mask++)
        {
            var roles = all.Where((_, index) => (mask & (1 << index)) != 0).ToHashSet();

            foreach (var hasAgency in new[] { true, false })
            {
                var allowed = _allowed.Any(entry => entry.HasAgency == hasAgency && roles.SetEquals(entry.Roles));
                data.Add(mask, hasAgency, allowed);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCombination))]
    public void Only_the_listed_combinations_are_allowed(int mask, bool hasAgency, bool expectedAllowed)
    {
        var roles = Enum.GetValues<Role>().Where((_, index) => (mask & (1 << index)) != 0).ToHashSet();

        var violations = RoleAssignmentRules.Validate(roles, hasAgency ? _agency : null);

        Assert.Equal(expectedAllowed, violations.Count == 0);
    }

    [Fact]
    public void The_table_really_covers_all_thirty_two_combinations()
    {
        Assert.Equal(32, EveryCombination().Count);
    }

    // --- Each rule on its own ---------------------------------------------------------------------

    [Fact]
    public void A_compliance_officer_must_also_be_a_registered_agent()
    {
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, Codes(Set(Role.AgencyComplianceOfficer), _agency));
        Assert.Empty(Codes(Set(Role.RegisteredAgent, Role.AgencyComplianceOfficer), _agency));
    }

    [Fact]
    public void A_compliance_officer_must_be_of_the_same_agency()
    {
        var roles = Set(Role.RegisteredAgent, Role.AgencyComplianceOfficer);

        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeOfOwnAgency, Codes(roles, _agency, officerOf: _otherAgency));
        Assert.Empty(Codes(roles, _agency, officerOf: _agency));
        Assert.Empty(Codes(roles, _agency, officerOf: null)); // unspecified means the user's own agency
    }

    [Fact]
    public void A_compliance_officer_of_an_agency_the_user_does_not_belong_to_is_refused_even_with_no_agency_given()
    {
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeOfOwnAgency, Codes(Set(Role.RegisteredAgent, Role.AgencyComplianceOfficer), null, officerOf: _agency));
    }

    [Theory]
    [InlineData(Role.Admin, Role.RegisteredAgent)]
    [InlineData(Role.Admin, Role.AgencyComplianceOfficer)]
    [InlineData(Role.CaseReviewer, Role.RegisteredAgent)]
    [InlineData(Role.CaseReviewer, Role.AgencyComplianceOfficer)]
    public void Admins_and_case_reviewers_cannot_hold_agent_roles(Role staff, Role agent)
    {
        Assert.Contains(RoleAssignmentRules.StaffCannotHoldAgentRoles, Codes(Set(staff, agent, Role.RegisteredAgent), _agency));
        Assert.Contains(RoleAssignmentRules.StaffCannotHoldAgentRoles, Codes(Set(staff, agent, Role.RegisteredAgent), null));
    }

    [Fact]
    public void Nobody_can_be_both_an_admin_and_a_case_reviewer()
    {
        Assert.Contains(RoleAssignmentRules.AdminCannotBeCaseReviewer, Codes(Set(Role.Admin, Role.CaseReviewer), null));
        Assert.Empty(Codes(Set(Role.Admin), null));
        Assert.Empty(Codes(Set(Role.CaseReviewer), null));
    }

    [Theory]
    [InlineData(Role.RegisteredAgent)]
    public void Every_agent_belongs_to_an_agency(Role agentRole)
    {
        Assert.Contains(RoleAssignmentRules.AgentRoleNeedsAgency, Codes(Set(agentRole), null));
        Assert.DoesNotContain(RoleAssignmentRules.AgentRoleNeedsAgency, Codes(Set(agentRole), _agency));
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.CaseReviewer)]
    public void Association_staff_belong_to_no_agency(Role staffRole)
    {
        Assert.Contains(RoleAssignmentRules.StaffMustHaveNoAgency, Codes(Set(staffRole), _agency));
        Assert.DoesNotContain(RoleAssignmentRules.StaffMustHaveNoAgency, Codes(Set(staffRole), null));
    }

    [Fact]
    public void Every_broken_rule_is_reported_not_just_the_first()
    {
        var codes = Codes(Set(Role.Admin, Role.CaseReviewer, Role.AgencyComplianceOfficer), _agency).ToList();

        Assert.Contains(RoleAssignmentRules.AdminCannotBeCaseReviewer, codes);
        Assert.Contains(RoleAssignmentRules.StaffCannotHoldAgentRoles, codes);
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, codes);
        Assert.Contains(RoleAssignmentRules.StaffMustHaveNoAgency, codes);
    }

    [Fact]
    public void Every_violation_has_a_readable_message()
    {
        var violations = RoleAssignmentRules.Validate(Set(Role.Admin, Role.CaseReviewer), _agency);

        Assert.All(violations, violation => Assert.False(string.IsNullOrWhiteSpace(violation.Message)));
    }

    [Fact]
    public void There_are_exactly_four_roles_and_they_are_classified_as_agent_or_staff()
    {
        Assert.Equal(
            ["Admin", "AgencyComplianceOfficer", "CaseReviewer", "RegisteredAgent"],
            Enum.GetNames<Role>().Order(StringComparer.Ordinal));
        Assert.Equal([Role.RegisteredAgent, Role.AgencyComplianceOfficer], Enum.GetValues<Role>().Where(role => role.IsAgentRole()));
        Assert.Equal([Role.CaseReviewer, Role.Admin], Enum.GetValues<Role>().Where(role => role.IsStaffRole()));
    }

    [Fact]
    public void There_are_exactly_four_membership_classes()
    {
        Assert.Equal(["Full", "Associate", "Student", "Honorary"], Enum.GetNames<MembershipClass>());
    }
}
