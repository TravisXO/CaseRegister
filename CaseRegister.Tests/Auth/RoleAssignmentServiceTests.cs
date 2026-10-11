using System.Net;
using CaseRegister.Application.Accounts;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

[Collection(PostgresTestGroup.Name)]
public class RoleAssignmentServiceTests(PostgresFixture postgres)
{
    private sealed record World(ApiFactory Factory, Agency Agency, ApplicationUser Member, ApplicationUser Staff) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public Task<RoleChangeResult> Add(ApplicationUser user, params Role[] roles) =>
            Factory.TryChangeRolesAsync(new RoleChange(user.Id, roles, []));

        public Task<RoleChangeResult> Remove(ApplicationUser user, params Role[] roles) =>
            Factory.TryChangeRolesAsync(new RoleChange(user.Id, [], roles));
    }

    private async Task<World> WorldAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync();
        var member = await factory.CreateUserAsync("member@example.test", agencyId: agency.Id);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);

        return new World(factory, agency, member, staff);
    }

    private static IEnumerable<string> Codes(RoleChangeResult result) => result.Violations.Select(violation => violation.Code);

    // --- The roles come from the migration --------------------------------------------------------

    [Fact]
    public async Task The_four_roles_exist_from_the_migration_and_the_application_never_created_them()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        _ = factory.CreateApiClient(); // start the application

        var roles = await factory.WithDbAsync(db => db.Roles.AsNoTracking().OrderBy(role => role.Name).ToListAsync());

        Assert.Equal(["Admin", "AgencyComplianceOfficer", "CaseReviewer", "RegisteredAgent"], roles.Select(role => role.Name));
        Assert.Equal(
            Enum.GetValues<Role>().Select(RoleSeed.IdOf).Order(),
            roles.Select(role => role.Id).Order());
    }

    [Fact]
    public async Task Identity_finds_each_role_by_name()
    {
        await using var factory = await ApiFactory.CreateAsync(postgres);
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var role in Enum.GetValues<Role>())
        {
            Assert.NotNull(await roleManager.FindByNameAsync(role.ToString()));
        }
    }

    [Fact]
    public async Task Changing_roles_does_not_create_any_role_it_only_assigns_the_existing_ones()
    {
        await using var world = await WorldAsync();

        await world.Add(world.Member, Role.RegisteredAgent, Role.AgencyComplianceOfficer);

        Assert.Equal(4, await world.Factory.WithDbAsync(db => db.Roles.CountAsync()));
    }

    // --- A legitimate change works ----------------------------------------------------------------

    [Fact]
    public async Task An_agency_member_can_be_made_a_registered_agent()
    {
        await using var world = await WorldAsync();

        var result = await world.Add(world.Member, Role.RegisteredAgent);

        Assert.True(result.Succeeded);
        Assert.Equal([Role.RegisteredAgent], await world.Factory.RolesOfAsync(world.Member));
    }

    [Fact]
    public async Task An_agent_can_be_made_a_compliance_officer_of_their_own_agency()
    {
        await using var world = await WorldAsync();
        await world.Add(world.Member, Role.RegisteredAgent);

        var result = await world.Factory.TryChangeRolesAsync(new RoleChange(world.Member.Id, [Role.AgencyComplianceOfficer], [], world.Agency.Id));

        Assert.True(result.Succeeded);
        Assert.Equal([Role.RegisteredAgent, Role.AgencyComplianceOfficer], await world.Factory.RolesOfAsync(world.Member));
    }

    [Fact]
    public async Task Both_agent_roles_can_be_given_in_one_change()
    {
        await using var world = await WorldAsync();

        Assert.True((await world.Add(world.Member, Role.RegisteredAgent, Role.AgencyComplianceOfficer)).Succeeded);
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.CaseReviewer)]
    public async Task Association_staff_can_be_made_an_admin_or_a_case_reviewer(Role role)
    {
        await using var world = await WorldAsync();

        Assert.True((await world.Add(world.Staff, role)).Succeeded);
        Assert.Equal([role], await world.Factory.RolesOfAsync(world.Staff));
    }

    // --- Each separation-of-duties rule, through the real service ----------------------------------

    [Fact]
    public async Task A_compliance_officer_must_also_be_a_registered_agent()
    {
        await using var world = await WorldAsync();

        var result = await world.Add(world.Member, Role.AgencyComplianceOfficer);

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, Codes(result));
        Assert.Empty(await world.Factory.RolesOfAsync(world.Member));
    }

    [Fact]
    public async Task A_compliance_officer_must_be_of_the_same_agency()
    {
        await using var world = await WorldAsync();
        var elsewhere = await world.Factory.CreateAgencyAsync("Another Agency");
        await world.Add(world.Member, Role.RegisteredAgent);

        var result = await world.Factory.TryChangeRolesAsync(new RoleChange(world.Member.Id, [Role.AgencyComplianceOfficer], [], elsewhere.Id));

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeOfOwnAgency, Codes(result));
        Assert.Equal([Role.RegisteredAgent], await world.Factory.RolesOfAsync(world.Member));
    }

    [Theory]
    [InlineData(Role.Admin)]
    [InlineData(Role.CaseReviewer)]
    public async Task An_agent_cannot_be_made_staff(Role staffRole)
    {
        await using var world = await WorldAsync();
        await world.Add(world.Member, Role.RegisteredAgent);

        var result = await world.Add(world.Member, staffRole);

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.StaffCannotHoldAgentRoles, Codes(result));
        Assert.Equal([Role.RegisteredAgent], await world.Factory.RolesOfAsync(world.Member));
    }

    [Theory]
    [InlineData(Role.RegisteredAgent)]
    [InlineData(Role.AgencyComplianceOfficer)]
    public async Task Staff_cannot_be_given_agent_roles(Role agentRole)
    {
        await using var world = await WorldAsync();
        await world.Add(world.Staff, Role.Admin);

        var result = await world.Add(world.Staff, agentRole);

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.StaffCannotHoldAgentRoles, Codes(result));
        Assert.Equal([Role.Admin], await world.Factory.RolesOfAsync(world.Staff));
    }

    [Fact]
    public async Task Nobody_can_be_both_an_admin_and_a_case_reviewer_added_one_after_the_other()
    {
        await using var world = await WorldAsync();
        await world.Add(world.Staff, Role.Admin);

        var result = await world.Add(world.Staff, Role.CaseReviewer);

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.AdminCannotBeCaseReviewer, Codes(result));
        Assert.Equal([Role.Admin], await world.Factory.RolesOfAsync(world.Staff));
    }

    [Fact]
    public async Task Nobody_can_be_both_an_admin_and_a_case_reviewer_added_together()
    {
        await using var world = await WorldAsync();

        var result = await world.Add(world.Staff, Role.Admin, Role.CaseReviewer);

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.AdminCannotBeCaseReviewer, Codes(result));
        Assert.Empty(await world.Factory.RolesOfAsync(world.Staff));
    }

    [Fact]
    public async Task A_case_reviewer_cannot_be_made_an_admin_either()
    {
        await using var world = await WorldAsync();
        await world.Add(world.Staff, Role.CaseReviewer);

        Assert.Contains(RoleAssignmentRules.AdminCannotBeCaseReviewer, Codes(await world.Add(world.Staff, Role.Admin)));
    }

    [Fact]
    public async Task Every_agent_must_belong_to_an_agency()
    {
        await using var world = await WorldAsync();

        var result = await world.Add(world.Staff, Role.RegisteredAgent); // the staff account has no agency

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.AgentRoleNeedsAgency, Codes(result));
    }

    [Fact]
    public async Task Association_staff_must_belong_to_no_agency()
    {
        await using var world = await WorldAsync();

        var result = await world.Add(world.Member, Role.Admin); // the member belongs to an agency

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.StaffMustHaveNoAgency, Codes(result));
    }

    [Fact]
    public async Task Taking_registered_agent_away_from_a_compliance_officer_is_refused_because_the_rules_apply_to_the_result()
    {
        await using var world = await WorldAsync();
        await world.Add(world.Member, Role.RegisteredAgent, Role.AgencyComplianceOfficer);

        var result = await world.Remove(world.Member, Role.RegisteredAgent);

        Assert.False(result.Succeeded);
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, Codes(result));
        Assert.Equal([Role.RegisteredAgent, Role.AgencyComplianceOfficer], await world.Factory.RolesOfAsync(world.Member));
    }

    [Fact]
    public async Task Both_agent_roles_can_be_removed_together_and_the_officer_role_alone()
    {
        await using var world = await WorldAsync();
        await world.Add(world.Member, Role.RegisteredAgent, Role.AgencyComplianceOfficer);

        Assert.True((await world.Remove(world.Member, Role.AgencyComplianceOfficer)).Succeeded);
        Assert.Equal([Role.RegisteredAgent], await world.Factory.RolesOfAsync(world.Member));

        await world.Add(world.Member, Role.AgencyComplianceOfficer);
        Assert.True((await world.Remove(world.Member, Role.RegisteredAgent, Role.AgencyComplianceOfficer)).Succeeded);
        Assert.Empty(await world.Factory.RolesOfAsync(world.Member));
    }

    [Fact]
    public async Task Every_broken_rule_is_reported()
    {
        await using var world = await WorldAsync();

        var result = await world.Add(world.Member, Role.Admin, Role.CaseReviewer, Role.AgencyComplianceOfficer);

        Assert.Contains(RoleAssignmentRules.AdminCannotBeCaseReviewer, Codes(result));
        Assert.Contains(RoleAssignmentRules.StaffCannotHoldAgentRoles, Codes(result));
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, Codes(result));
        Assert.Contains(RoleAssignmentRules.StaffMustHaveNoAgency, Codes(result));
    }

    [Fact]
    public async Task A_change_for_nobody_is_reported_as_no_such_user()
    {
        await using var world = await WorldAsync();

        var result = await world.Factory.TryChangeRolesAsync(new RoleChange(Guid.NewGuid(), [Role.RegisteredAgent], []));

        Assert.True(result.UserNotFound);
        Assert.False(result.Succeeded);
    }

    // --- A refused change changes nothing -----------------------------------------------------------

    [Fact]
    public async Task A_refused_change_leaves_the_roles_the_stamp_the_sessions_and_the_audit_log_alone()
    {
        await using var world = await WorldAsync();
        await world.Add(world.Staff, Role.Admin);
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.Staff);
        var stampBefore = (await world.Factory.GetUserAsync("staff@example.test")).SecurityStamp;
        var auditBefore = await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(record => record.Action == "RolesChanged"));

        var result = await world.Add(world.Staff, Role.CaseReviewer, Role.RegisteredAgent);

        Assert.False(result.Succeeded);
        Assert.Equal([Role.Admin], await world.Factory.RolesOfAsync(world.Staff));
        Assert.Equal(stampBefore, (await world.Factory.GetUserAsync("staff@example.test")).SecurityStamp);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode); // still signed in
        Assert.Equal(auditBefore, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(record => record.Action == "RolesChanged")));
    }

    [Fact]
    public async Task Asking_for_a_role_the_user_already_has_changes_nothing_and_signs_nobody_out()
    {
        await using var world = await WorldAsync();
        await world.Add(world.Staff, Role.Admin);
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.Staff);
        var stampBefore = (await world.Factory.GetUserAsync("staff@example.test")).SecurityStamp;

        var result = await world.Add(world.Staff, Role.Admin);

        Assert.True(result.Succeeded);
        Assert.Equal(stampBefore, (await world.Factory.GetUserAsync("staff@example.test")).SecurityStamp);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/members-only")).StatusCode);
    }

    // --- A role change takes effect immediately ------------------------------------------------------

    [Fact]
    public async Task A_role_change_gives_the_user_a_new_security_stamp()
    {
        await using var world = await WorldAsync();
        var before = (await world.Factory.GetUserAsync("member@example.test")).SecurityStamp;

        await world.Add(world.Member, Role.RegisteredAgent);

        Assert.NotEqual(before, (await world.Factory.GetUserAsync("member@example.test")).SecurityStamp);
    }

    [Fact]
    public async Task A_role_change_ends_every_session_of_that_user_at_once_and_nobody_elses()
    {
        await using var world = await WorldAsync();
        var bystander = await world.Factory.CreateUserAsync("bystander@example.test");
        var laptop = world.Factory.CreateApiClient();
        var phone = world.Factory.CreateApiClient();
        var other = world.Factory.CreateApiClient();
        await laptop.SignInWithTwoFactorAsync(world.Factory, world.Member);
        await phone.SignInWithTwoFactorAsync(world.Factory, world.Member);
        await other.SignInWithTwoFactorAsync(world.Factory, bystander);
        Assert.Equal(2, await world.Factory.WithDbAsync(db => db.Sessions.CountAsync(session => session.UserId == world.Member.Id)));

        await world.Add(world.Member, Role.RegisteredAgent);

        // No clock moved: not a timeout and not the 5-minute stamp check. The sessions are simply gone.
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/probe/members-only")).StatusCode);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Sessions.CountAsync(session => session.UserId == world.Member.Id)));
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/probe/members-only")).StatusCode);
    }

    [Fact]
    public async Task A_granted_role_is_in_the_next_session_and_not_before()
    {
        await using var world = await WorldAsync();
        await world.Factory.AddCertificateAsync(world.Member);
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, world.Member);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);

        await world.Add(world.Member, Role.RegisteredAgent);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode); // the old session is gone

        await client.SignInWithTwoFactorAsync(world.Factory, world.Member);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);
    }

    [Fact]
    public async Task A_taken_away_role_is_gone_from_the_next_session()
    {
        await using var world = await WorldAsync();
        await world.Factory.AddCertificateAsync(world.Member);
        await world.Add(world.Member, Role.RegisteredAgent);
        var client = world.Factory.CreateApiClient();
        await client.SignInAsync("member@example.test");
        await client.VerifyTwoFactorAsync(world.Factory.NextTotpCode(world.Member));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);

        await world.Remove(world.Member, Role.RegisteredAgent);
        await client.SignInWithTwoFactorAsync(world.Factory, world.Member);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);
    }

    [Fact]
    public async Task A_role_change_is_audited_with_who_asked_for_it_and_which_roles_but_nothing_personal()
    {
        await using var world = await WorldAsync();
        var admin = await world.Factory.CreateUserAsync("admin@example.test", staff: true);

        await world.Factory.TryChangeRolesAsync(new RoleChange(world.Member.Id, [Role.RegisteredAgent, Role.AgencyComplianceOfficer], [], ChangedByUserId: admin.Id));

        var entry = Assert.Single(await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(record => record.Action == "RolesChanged").ToListAsync()));
        Assert.Equal(admin.Id, entry.ActorUserId);
        Assert.Equal(world.Member.Id.ToString(), entry.Subject);
        // Before and after are there too, so a change can be read without looking anything else up.
        Assert.Equal("added: AgencyComplianceOfficer,RegisteredAgent; removed: ; before: ; after: RegisteredAgent,AgencyComplianceOfficer", entry.Detail);
        Assert.DoesNotContain("member@example.test", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_are_no_endpoints_for_changing_roles_yet()
    {
        await using var world = await WorldAsync();
        var admin = await world.Factory.CreateStaffAsync("admin@example.test", Role.Admin);
        var client = world.Factory.CreateApiClient();
        await client.SignInWithTwoFactorAsync(world.Factory, admin);

        foreach (var path in new[] { "/api/roles", "/api/users", $"/api/users/{world.Member.Id}/roles", "/api/admin/roles" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(HttpMethod.Post, path, new { role = "Admin" })).StatusCode);
        }
    }
}
