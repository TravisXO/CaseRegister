using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Tests.Auth;

/// <summary>What each admin action does, and how it is reached: by two admins, or at once by one.</summary>
[Collection(PostgresTestGroup.Name)]
public class AdminActionTests(PostgresFixture postgres)
{
    private sealed record World(
        ApiFactory Factory,
        ApplicationUser A,
        ApplicationUser B,
        ApplicationUser C,
        Agency Agency,
        ApplicationUser Member,
        ApplicationUser Officer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public async Task<ApiClient> SignInAsync(ApplicationUser user)
        {
            var client = Factory.CreateApiClient();
            Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(Factory, user)).StatusCode);

            return client;
        }

        public static async Task<HttpStatusCode> ProfileStatusAsync(ApiClient client) => (await client.GetAsync("/api/me")).StatusCode;

        public static async Task<HttpStatusCode> CanAsync(ApiClient client, string policy) => (await client.GetAsync($"/probe/can/{policy}")).StatusCode;

        public Task<ApplicationUser> Fresh(ApplicationUser user) => Factory.GetUserAsync(user.Email!);

        public Task<int> SessionCount(ApplicationUser user) => Factory.WithDbAsync(db => db.Sessions.CountAsync(session => session.UserId == user.Id));

        public Task<int> AuditCount(string action) => Factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == action));
    }

    private async Task<World> WorldAsync()
    {
        var factory = await ApiFactory.CreateAsync(postgres);
        var agency = await factory.CreateAgencyAsync("Copperbelt Homes");
        var admins = await factory.CreateAdminsAsync(3);
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var officer = await factory.CreateMemberAsync("officer@example.test", agency, [Role.RegisteredAgent, Role.AgencyComplianceOfficer]);

        return new World(factory, admins[0], admins[1], admins[2], agency, member, officer);
    }

    private static string[] Codes(IEnumerable<ApprovalViolation>? problems) => [.. (problems ?? []).Select(problem => problem.Code).Order(StringComparer.Ordinal)];

    // --- Roles ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Giving_a_role_needs_two_admins_and_ends_the_persons_sessions_at_once()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Member);
        Assert.Equal(HttpStatusCode.OK, await World.ProfileStatusAsync(client));
        var stampBefore = (await world.Fresh(world.Member)).SecurityStamp;

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.AssignRole(world.Member, Role.AgencyComplianceOfficer));

        Assert.Equal([Role.RegisteredAgent, Role.AgencyComplianceOfficer], await world.Factory.RolesOfAsync(world.Member));
        Assert.Equal(0, await world.SessionCount(world.Member));
        Assert.NotEqual(stampBefore, (await world.Fresh(world.Member)).SecurityStamp);
        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(client)); // the old session is gone, with no clock movement

        var again = await world.SignInAsync(world.Member);
        var profile = await (await again.GetAsync("/api/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("AgencyComplianceOfficer", profile.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
    }

    [Fact]
    public async Task Taking_a_role_away_needs_two_admins_too()
    {
        await using var world = await WorldAsync();

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.RemoveRole(world.Officer, Role.AgencyComplianceOfficer));

        Assert.Equal([Role.RegisteredAgent], await world.Factory.RolesOfAsync(world.Officer));
    }

    [Fact]
    public async Task A_role_change_that_breaks_a_separation_rule_is_refused_when_proposed()
    {
        await using var world = await WorldAsync();

        var admin = await world.Factory.ProposeAsync(world.A, Proposals.AssignRole(world.Member, Role.Admin)); // an agent of an agency
        var withoutAgent = await world.Factory.ProposeAsync(world.A, Proposals.RemoveRole(world.Officer, Role.RegisteredAgent)); // a compliance officer must stay an agent

        Assert.Equal(ProposeOutcome.Refused, admin.Outcome);
        Assert.Contains("StaffMustHaveNoAgency", Codes(admin.Problems));
        Assert.Equal(ProposeOutcome.Refused, withoutAgent.Outcome);
        Assert.Contains(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, Codes(withoutAgent.Problems));
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task A_role_the_person_already_holds_cannot_be_proposed_again_and_nor_can_one_they_lack_be_taken_away()
    {
        await using var world = await WorldAsync();

        var held = await world.Factory.ProposeAsync(world.A, Proposals.AssignRole(world.Member, Role.RegisteredAgent));
        var absent = await world.Factory.ProposeAsync(world.A, Proposals.RemoveRole(world.Member, Role.AgencyComplianceOfficer));

        Assert.Equal([ProposeOutcome.Refused, ProposeOutcome.Refused], [held.Outcome, absent.Outcome]);
        Assert.Equal(["NothingToChange"], Codes(held.Problems));
        Assert.Equal(["NothingToChange"], Codes(absent.Problems));
    }

    // --- Reactivating an account ---------------------------------------------------------------------------

    [Fact]
    public async Task Reactivating_a_suspended_account_needs_two_admins_and_clears_the_lockout()
    {
        await using var world = await WorldAsync();
        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendUserAsync(world.A, world.Member)).Outcome);
        await world.Factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Id == world.Member.Id);
            user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(5);
            user.AccessFailedCount = 4;
            await db.SaveChangesAsync();

            return 0;
        });
        var client = world.Factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SignInAsync(world.Member.Email!)).StatusCode);

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ReactivateUser, world.Member));
        Assert.Equal(AccountStatus.Suspended, (await world.Fresh(world.Member)).AccountStatus); // nothing until confirmed
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        var reactivated = await world.Fresh(world.Member);
        Assert.Equal(AccountStatus.Approved, reactivated.AccountStatus);
        Assert.Null(reactivated.LockoutEnd);
        Assert.Equal(0, reactivated.AccessFailedCount);
        await world.SignInAsync(world.Member); // and they can sign in again (with their two-factor, as ever)
        Assert.Equal(1, await world.AuditCount("UserReactivated"));
    }

    [Fact]
    public async Task Only_a_suspended_account_can_be_reactivated()
    {
        await using var world = await WorldAsync();

        var result = await world.Factory.ProposeAsync(world.A, Proposals.ForUser(PendingActionType.ReactivateUser, world.Member));

        Assert.Equal(ProposeOutcome.Refused, result.Outcome);
        Assert.Equal(["TargetStatusChanged"], Codes(result.Problems));
    }

    // --- Agencies --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Suspending_an_agency_takes_the_agent_capabilities_from_every_member_at_once_and_only_them()
    {
        await using var world = await WorldAsync();
        var otherAgency = await world.Factory.CreateAgencyAsync("Lusaka Properties");
        var outsider = await world.Factory.CreateMemberAsync("outsider@example.test", otherAgency, [Role.RegisteredAgent]);
        var reviewer = await world.Factory.CreateStaffAsync("reviewer@example.test", Role.CaseReviewer);
        var memberClient = await world.SignInAsync(world.Member);
        var officerClient = await world.SignInAsync(world.Officer);
        var outsiderClient = await world.SignInAsync(outsider);
        var reviewerClient = await world.SignInAsync(reviewer);
        foreach (var client in new[] { memberClient, officerClient, outsiderClient })
        {
            Assert.Equal(HttpStatusCode.OK, await World.CanAsync(client, "RunPreDealCheck"));
        }

        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id)).Outcome);

        // The same sessions, with no sign-out: the very next request is refused.
        Assert.Equal(HttpStatusCode.Forbidden, await World.CanAsync(memberClient, "RunPreDealCheck"));
        Assert.Equal(HttpStatusCode.Forbidden, await World.CanAsync(officerClient, "RunPreDealCheck"));
        Assert.Equal(HttpStatusCode.Forbidden, await World.CanAsync(officerClient, "FileFicReports"));
        Assert.Equal(HttpStatusCode.Forbidden, await World.CanAsync(officerClient, "ViewComplianceStatus"));

        // Everyone else is untouched, and the members can still see their own profile.
        Assert.Equal(HttpStatusCode.OK, await World.CanAsync(outsiderClient, "RunPreDealCheck"));
        Assert.Equal(HttpStatusCode.OK, await World.CanAsync(reviewerClient, "RunPreDealCheck"));
        Assert.Equal(HttpStatusCode.OK, await World.ProfileStatusAsync(memberClient));
        Assert.Equal(AgencyStatus.Suspended, (await world.Factory.WithDbAsync(db => db.Agencies.AsNoTracking().SingleAsync(a => a.Id == world.Agency.Id))).Status);
    }

    [Fact]
    public async Task Reactivating_an_agency_needs_two_admins_and_gives_the_capabilities_back()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Member);
        await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id);
        Assert.Equal(HttpStatusCode.Forbidden, await World.CanAsync(client, "RunPreDealCheck"));

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForAgency(PendingActionType.ReactivateAgency, world.Agency.Id));
        Assert.Equal(HttpStatusCode.Forbidden, await World.CanAsync(client, "RunPreDealCheck")); // nothing until confirmed
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        Assert.Equal(HttpStatusCode.OK, await World.CanAsync(client, "RunPreDealCheck"));
        Assert.Equal("Copperbelt Homes", proposal.TargetName);
    }

    [Fact]
    public async Task Only_a_suspended_agency_can_be_reactivated_and_only_an_active_one_suspended()
    {
        await using var world = await WorldAsync();

        var reactivate = await world.Factory.ProposeAsync(world.A, Proposals.ForAgency(PendingActionType.ReactivateAgency, world.Agency.Id));
        await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id);
        var again = await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id);

        Assert.Equal(ProposeOutcome.Refused, reactivate.Outcome);
        Assert.Equal(["TargetStatusChanged"], Codes(reactivate.Problems));
        Assert.Equal(AdminActionOutcome.Refused, again.Outcome);
    }

    [Fact]
    public async Task Suspending_an_agency_that_does_not_exist_is_not_found()
    {
        await using var world = await WorldAsync();

        Assert.Equal(AdminActionOutcome.NotFound, (await world.Factory.SuspendAgencyAsync(world.A, Guid.NewGuid())).Outcome);
        Assert.Equal(ProposeOutcome.TargetNotFound, (await world.Factory.ProposeAsync(world.A, Proposals.ForAgency(PendingActionType.ReactivateAgency, Guid.NewGuid()))).Outcome);
    }

    // --- Resetting two-factor ----------------------------------------------------------------------------

    [Fact]
    public async Task Resetting_two_factor_removes_the_authenticator_and_recovery_codes_ends_sessions_and_requires_enrolment()
    {
        await using var world = await WorldAsync();
        var person = await world.Factory.CreateMemberAsync("person@example.test", world.Agency, [Role.RegisteredAgent], twoFactor: false);
        var enrolling = await TwoFactorFlows.RestrictedSessionAsync(world.Factory, person);
        var oldCodes = await TwoFactorFlows.EnrolFullyAsync(world.Factory, enrolling, person);
        var client = await world.SignInAsync(person);
        Assert.Equal(HttpStatusCode.OK, await World.ProfileStatusAsync(client));
        Assert.Equal(oldCodes.Count, await world.Factory.WithDbAsync(db => db.RecoveryCodes.CountAsync(code => code.UserId == person.Id && code.RevokedAt == null)));

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ResetTwoFactor, person));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == person.Id))); // not yet
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        // The authenticator and the codes are gone as far as the application is concerned, but kept as history.
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == person.Id)));
        var removed = await world.Factory.WithDbAsync(db => db.Authenticators.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.UserId == person.Id));
        Assert.NotNull(removed.RemovedAt);
        Assert.Equal(oldCodes.Count, await world.Factory.WithDbAsync(db => db.RecoveryCodes.CountAsync(code => code.UserId == person.Id && code.RevokedAt != null)));
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.RecoveryCodes.CountAsync(code => code.UserId == person.Id && code.RevokedAt == null)));
        Assert.False((await world.Fresh(person)).TwoFactorEnabled);
        Assert.Equal(0, await world.SessionCount(person));
        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(client));

        // At the next sign-in the password alone gives only a session that can enrol, and nothing else.
        var restricted = await TwoFactorFlows.RestrictedSessionAsync(world.Factory, person);
        Assert.Equal(HttpStatusCode.Forbidden, await World.ProfileStatusAsync(restricted));

        // They enrol again with a new key, and are back to normal.
        var newCodes = await TwoFactorFlows.EnrolFullyAsync(world.Factory, restricted, person);
        Assert.DoesNotContain(newCodes, oldCodes.Contains);
        await world.SignInAsync(person);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == person.Id)));
        Assert.Equal(2, await world.Factory.WithDbAsync(db => db.Authenticators.IgnoreQueryFilters().CountAsync(a => a.UserId == person.Id))); // the old one is still there
        Assert.Equal(1, await world.AuditCount("TwoFactorReset"));
    }

    [Fact]
    public async Task Resetting_two_factor_for_an_account_that_has_none_is_refused()
    {
        await using var world = await WorldAsync();
        var bare = await world.Factory.CreateUserAsync("bare@example.test", twoFactor: false);

        var result = await world.Factory.ProposeAsync(world.A, Proposals.ForUser(PendingActionType.ResetTwoFactor, bare));

        Assert.Equal(ProposeOutcome.Refused, result.Outcome);
        Assert.Equal(["NothingToReset"], Codes(result.Problems));
    }

    // --- Taking access away at once ----------------------------------------------------------------------

    [Fact]
    public async Task One_admin_suspends_a_non_admin_and_their_sessions_end_at_once()
    {
        await using var world = await WorldAsync();
        var first = await world.SignInAsync(world.Member);
        var second = await world.SignInAsync(world.Member);
        var stampBefore = (await world.Fresh(world.Member)).SecurityStamp;

        var result = await world.Factory.SuspendUserAsync(world.A, world.Member, "Repeated complaints about conduct, under review.");

        Assert.Equal(AdminActionOutcome.Done, result.Outcome);
        Assert.Equal(AccountStatus.Suspended, (await world.Fresh(world.Member)).AccountStatus);
        Assert.NotEqual(stampBefore, (await world.Fresh(world.Member)).SecurityStamp);
        Assert.Equal(0, await world.SessionCount(world.Member));
        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(first));
        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(second));
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.CreateApiClient().SignInAsync(world.Member.Email!)).StatusCode);
    }

    [Fact]
    public async Task An_immediate_suspension_is_logged_with_who_why_and_before_and_after()
    {
        await using var world = await WorldAsync();

        await world.Factory.SuspendUserAsync(world.A, world.Member, "Repeated complaints about conduct, under review.");

        var entry = Assert.Single(await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Where(e => e.Action == "UserSuspended").ToListAsync()));
        Assert.Equal(world.A.Id, entry.ActorUserId);
        Assert.Equal(world.Member.Id.ToString(), entry.Subject);
        Assert.Contains("immediate", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("before: status: Approved", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("after: status: Suspended; all sessions ended", entry.Detail, StringComparison.Ordinal);
        Assert.Contains("Repeated complaints about conduct, under review.", entry.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too short")]
    public async Task Every_immediate_action_needs_a_proper_reason_and_changes_nothing_without_one(string reason)
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.Member);

        Assert.Equal(AdminActionOutcome.Invalid, (await world.Factory.SuspendUserAsync(world.A, world.Member, reason)).Outcome);
        Assert.Equal(AdminActionOutcome.Invalid, (await world.Factory.EndSessionsAsync(world.A, world.Member, reason)).Outcome);
        Assert.Equal(AdminActionOutcome.Invalid, (await world.Factory.SuspendAgencyAsync(world.A, world.Agency.Id, reason)).Outcome);

        Assert.Equal(AccountStatus.Approved, (await world.Fresh(world.Member)).AccountStatus);
        Assert.Equal(HttpStatusCode.OK, await World.ProfileStatusAsync(client));
        Assert.Equal(AgencyStatus.Active, (await world.Factory.WithDbAsync(db => db.Agencies.AsNoTracking().SingleAsync(a => a.Id == world.Agency.Id))).Status);
    }

    [Fact]
    public async Task Nobody_can_suspend_or_sign_out_themselves()
    {
        await using var world = await WorldAsync();

        var suspend = await world.Factory.SuspendUserAsync(world.A, world.A);
        var end = await world.Factory.EndSessionsAsync(world.A, world.A);

        Assert.Equal(AdminActionOutcome.NotAllowed, suspend.Outcome);
        Assert.Equal([ApprovalRules.SelfTarget], Codes(suspend.Problems));
        Assert.Equal(AdminActionOutcome.NotAllowed, end.Outcome);
        Assert.Equal(AccountStatus.Approved, (await world.Fresh(world.A)).AccountStatus);
    }

    [Fact]
    public async Task Only_an_active_admin_can_use_the_immediate_actions()
    {
        await using var world = await WorldAsync();
        var reviewer = await world.Factory.CreateStaffAsync("reviewer@example.test", Role.CaseReviewer);
        var suspendedAdmin = await world.Factory.CreateStaffAsync("suspended@example.test", Role.Admin);
        await world.Factory.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == suspendedAdmin.Id)).AccountStatus = AccountStatus.Suspended;
            await db.SaveChangesAsync();

            return 0;
        });

        foreach (var notAdmin in new[] { world.Officer, reviewer, suspendedAdmin })
        {
            Assert.Equal(AdminActionOutcome.NotAnAdmin, (await world.Factory.SuspendUserAsync(notAdmin, world.Member)).Outcome);
            Assert.Equal(AdminActionOutcome.NotAnAdmin, (await world.Factory.EndSessionsAsync(notAdmin, world.Member)).Outcome);
            Assert.Equal(AdminActionOutcome.NotAnAdmin, (await world.Factory.SuspendAgencyAsync(notAdmin, world.Agency.Id)).Outcome);
        }

        Assert.Equal(AccountStatus.Approved, (await world.Fresh(world.Member)).AccountStatus);
    }

    [Fact]
    public async Task Suspending_someone_who_does_not_exist_or_is_already_suspended_does_nothing()
    {
        await using var world = await WorldAsync();
        await world.Factory.SuspendUserAsync(world.A, world.Member);

        var again = await world.Factory.SuspendUserAsync(world.A, world.Member);
        var stranger = await world.Factory.SuspendUserAsync(world.A, new ApplicationUser { Id = Guid.NewGuid() });

        Assert.Equal(AdminActionOutcome.Refused, again.Outcome);
        Assert.Equal(["TargetStatusChanged"], Codes(again.Problems));
        Assert.Equal(AdminActionOutcome.NotFound, stranger.Outcome);
        Assert.Equal(1, await world.AuditCount("UserSuspended"));
    }

    [Fact]
    public async Task Ending_sessions_signs_the_person_out_everywhere_and_leaves_their_account_alone()
    {
        await using var world = await WorldAsync();
        var first = await world.SignInAsync(world.Member);
        var second = await world.SignInAsync(world.Member);
        var other = await world.SignInAsync(world.Officer);

        var result = await world.Factory.EndSessionsAsync(world.A, world.Member);

        Assert.Equal(AdminActionOutcome.Done, result.Outcome);
        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(first));
        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(second));
        Assert.Equal(HttpStatusCode.OK, await World.ProfileStatusAsync(other)); // someone else's session is untouched
        Assert.Equal(AccountStatus.Approved, (await world.Fresh(world.Member)).AccountStatus);
        await world.SignInAsync(world.Member); // and they can sign in again
        Assert.Equal(1, await world.AuditCount("SessionsEnded"));
    }

    // --- Admins are different: they need two ---------------------------------------------------------------

    [Fact]
    public async Task A_single_admin_cannot_suspend_or_end_the_sessions_of_another_admin()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.B);

        var suspend = await world.Factory.SuspendUserAsync(world.A, world.B);
        var end = await world.Factory.EndSessionsAsync(world.A, world.B);

        Assert.Equal(AdminActionOutcome.NeedsTwoAdmins, suspend.Outcome);
        Assert.Equal(AdminActionOutcome.NeedsTwoAdmins, end.Outcome);
        Assert.Equal(AccountStatus.Approved, (await world.Fresh(world.B)).AccountStatus);
        Assert.Equal(HttpStatusCode.OK, await World.ProfileStatusAsync(client)); // nothing happened to B
        Assert.Equal(0, await world.AuditCount("UserSuspended"));
        Assert.Equal(0, await world.AuditCount("SessionsEnded"));
    }

    [Fact]
    public async Task Two_admins_can_suspend_another_admin_through_a_proposal()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.C);

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.SuspendUser, world.C));
        Assert.Equal(AccountStatus.Approved, (await world.Fresh(world.C)).AccountStatus); // not until confirmed
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        Assert.Equal(AccountStatus.Suspended, (await world.Fresh(world.C)).AccountStatus);
        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(client));
        Assert.Equal(1, await world.AuditCount("UserSuspended"));
    }

    [Fact]
    public async Task Two_admins_can_end_another_admins_sessions_through_a_proposal()
    {
        await using var world = await WorldAsync();
        var client = await world.SignInAsync(world.C);

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.ForUser(PendingActionType.EndSessions, world.C));

        Assert.Equal(HttpStatusCode.Unauthorized, await World.ProfileStatusAsync(client));
        Assert.Equal(AccountStatus.Approved, (await world.Fresh(world.C)).AccountStatus);
    }

    [Fact]
    public async Task What_one_admin_can_do_at_once_is_not_proposed()
    {
        await using var world = await WorldAsync();

        foreach (var command in new[]
        {
            Proposals.ForUser(PendingActionType.SuspendUser, world.Member),
            Proposals.ForUser(PendingActionType.EndSessions, world.Member),
            Proposals.ForAgency(PendingActionType.SuspendAgency, world.Agency.Id),
        })
        {
            Assert.Equal(ProposeOutcome.NoApprovalNeeded, (await world.Factory.ProposeAsync(world.A, command)).Outcome);
        }

        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    // --- Proposing the wrong thing -------------------------------------------------------------------------------

    [Fact]
    public async Task A_proposal_must_carry_exactly_what_its_type_needs()
    {
        await using var world = await WorldAsync();
        var reason = Proposals.Reason;

        foreach (var bad in new[]
        {
            new ProposeActionCommand(PendingActionType.AssignRole, world.Member.Id, null, null, null, reason), // no role
            new ProposeActionCommand(PendingActionType.AssignRole, world.Member.Id, null, Role.Admin, Guid.NewGuid(), reason), // a stray certificate
            new ProposeActionCommand(PendingActionType.AssignRole, null, world.Agency.Id, Role.Admin, null, reason), // an agency
            new ProposeActionCommand(PendingActionType.VerifyPractisingCertificate, world.Member.Id, null, null, null, reason), // no certificate
            new ProposeActionCommand(PendingActionType.ResetTwoFactor, world.Member.Id, null, Role.Admin, null, reason), // a stray role
            new ProposeActionCommand(PendingActionType.ReactivateAgency, world.Member.Id, null, null, null, reason), // a user, not an agency
            new ProposeActionCommand(PendingActionType.ReactivateUser, null, null, null, null, reason), // no target
            new ProposeActionCommand(PendingActionType.ReactivateUser, world.Member.Id, world.Agency.Id, null, null, reason), // two targets
            new ProposeActionCommand((PendingActionType)999, world.Member.Id, null, null, null, reason), // not a type
        })
        {
            Assert.Equal(ProposeOutcome.Invalid, (await world.Factory.ProposeAsync(world.A, bad)).Outcome);
        }

        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task Proposing_for_someone_who_does_not_exist_is_not_found()
    {
        await using var world = await WorldAsync();

        var result = await world.Factory.ProposeAsync(world.A, new ProposeActionCommand(PendingActionType.ResetTwoFactor, Guid.NewGuid(), null, null, null, Proposals.Reason));

        Assert.Equal(ProposeOutcome.TargetNotFound, result.Outcome);
    }
}
