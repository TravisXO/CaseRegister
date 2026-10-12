using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CaseRegister.Application.Applications;
using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Certificates;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>Reviewing applications: suggestions, approval as one all-or-nothing action, flags, and rejection.</summary>
[Collection(PostgresTestGroup.Name)]
public class ApplicationReviewTests(PostgresFixture postgres)
{
    private const string Email = "jane@example.test";

    private sealed record World(ApiFactory Factory, ApplicationUser A, ApplicationUser B, ApplicationUser C, ApplicationUser Applicant) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public Task<MemberApplication> Application() =>
            Factory.WithDbAsync(db => db.MemberApplications.AsNoTracking().SingleAsync(application => application.UserId == Applicant.Id));

        public Task<ApplicationUser> Fresh() => Factory.GetUserAsync(Applicant.Email!);
    }

    private async Task<World> WorldAsync(Action<Dictionary<string, string?>>? settings = null)
    {
        var factory = await ApiFactory.CreateAsync(postgres, settings);
        var admins = await factory.CreateAdminsAsync(3);
        var applicant = await SignUps.ApplyAsync(factory, Email, SignUps.Body(factory, Email, membershipNumber: "AGT 884", agencyName: "Copperbelt Homes Ltd", membershipClass: "Associate"));

        return new World(factory, admins[0], admins[1], admins[2], applicant);
    }

    private static string[] Codes(IEnumerable<ApprovalViolation>? problems) => [.. (problems ?? []).Select(problem => problem.Code).Order(StringComparer.Ordinal)];

    private static async Task<ApiClient> SignInAsync(ApiFactory factory, ApplicationUser user)
    {
        var client = factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.OK, (await client.SignInWithTwoFactorAsync(factory, user)).StatusCode);

        return client;
    }

    // --- The list ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Admins_list_the_waiting_applications_with_their_details_and_flags()
    {
        await using var world = await WorldAsync();
        await world.Factory.CreateUserAsync("holder@example.test", membershipNumber: 901);
        await SignUps.ApplyAsync(world.Factory, "second@example.test", SignUps.Body(world.Factory, "second@example.test", membershipNumber: "AGT 901", agencyName: "Lusaka Realty"));
        var admin = await SignInAsync(world.Factory, world.A);

        var response = await admin.GetAsync("/api/admin/applications");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        var jane = list.Single(item => item.GetProperty("email").GetString() == Email);
        Assert.Equal(world.Applicant.Id, jane.GetProperty("userId").GetGuid());
        Assert.Equal("Jane Mwansa", jane.GetProperty("fullName").GetString());
        Assert.Equal("AGT 884", jane.GetProperty("membershipNumber").GetString());
        Assert.Equal("Associate", jane.GetProperty("membershipClass").GetString());
        Assert.Equal("Copperbelt Homes Ltd", jane.GetProperty("agencyName").GetString());
        Assert.Equal(world.Factory.Today.Year, jane.GetProperty("certificateYear").GetInt32());
        Assert.Empty(jane.GetProperty("flags").EnumerateArray());
        var second = list.Single(item => item.GetProperty("email").GetString() == "second@example.test");
        Assert.Equal(["DuplicateMembershipNumber"], second.GetProperty("flags").EnumerateArray().Select(flag => flag.GetString()));
    }

    [Fact]
    public async Task Only_waiting_applications_are_listed()
    {
        await using var world = await WorldAsync();
        var rejected = await SignUps.ApplyAsync(world.Factory, "rejected@example.test");
        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.RejectApplication(rejected));
        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));

        using var scope = world.Factory.Services.CreateScope();
        var listed = await scope.ServiceProvider.GetRequiredService<IApplicationReviewService>().ListPendingAsync();

        Assert.Empty(listed);
    }

    [Fact]
    public async Task Existing_agencies_with_the_same_name_once_case_punctuation_and_endings_are_ignored_are_suggested_and_nothing_is_merged()
    {
        await using var world = await WorldAsync();
        var copperbelt = await world.Factory.CreateAgencyAsync("COPPERBELT HOMES, LIMITED");
        await world.Factory.CreateAgencyAsync("Copperbelt Homes Zambia"); // a different name: not suggested
        await world.Factory.CreateAgencyAsync("Lusaka Realty");
        var suspended = await world.Factory.CreateAgencyAsync("copperbelt homes");
        await world.Factory.SuspendAgencyAsync(world.A, suspended.Id); // not an agency to join: not suggested
        var agenciesBefore = await world.Factory.WithDbAsync(db => db.Agencies.CountAsync());

        using var scope = world.Factory.Services.CreateScope();
        var listed = await scope.ServiceProvider.GetRequiredService<IApplicationReviewService>().ListPendingAsync();

        var match = Assert.Single(Assert.Single(listed).SuggestedAgencies);
        Assert.Equal(copperbelt.Id, match.Id);
        Assert.Equal("COPPERBELT HOMES, LIMITED", match.Name);

        // Listing, suggesting and even signing up changed nothing: no agency made, none linked.
        Assert.Equal(agenciesBefore, await world.Factory.WithDbAsync(db => db.Agencies.CountAsync()));
        Assert.Null((await world.Fresh()).AgencyId);
    }

    // --- Approving ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_with_an_existing_agency_does_everything_in_one_go()
    {
        await using var world = await WorldAsync();
        var agency = await world.Factory.CreateAgencyAsync("Copperbelt Homes");
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, existingAgencyId: agency.Id));

        // Proposing changes nothing at all.
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PractisingCertificates.CountAsync()));

        await world.Factory.ConfirmOkAsync(world.B, proposal);

        var user = await world.Fresh();
        Assert.Equal(AccountStatus.Approved, user.AccountStatus);
        Assert.Equal(884, user.MembershipNumber!.Value);
        Assert.Equal(MembershipClass.Associate, user.MembershipClass);
        Assert.Equal(agency.Id, user.AgencyId);
        Assert.Equal([Role.RegisteredAgent], await world.Factory.RolesOfAsync(user));

        var certificate = await world.Factory.WithDbAsync(db => db.PractisingCertificates.AsNoTracking().SingleAsync());
        Assert.Equal(user.Id, certificate.UserId);
        Assert.True(certificate.IsVerified);
        Assert.Equal(world.B.Id, certificate.VerifiedByUserId); // the second admin made it true
        Assert.Equal(new DateOnly(world.Factory.Today.Year, 1, 1), certificate.ValidFrom);
        Assert.Equal(new DateOnly(world.Factory.Today.Year, 12, 31), certificate.ValidTo);

        var application = await world.Application();
        Assert.Equal(ApplicationStatus.Approved, application.Status);
        Assert.Equal(agency.Id, application.ApprovedAgencyId);
        Assert.Equal(world.B.Id, application.DecidedByUserId);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Agencies.CountAsync())); // no extra agency
        using var scope = world.Factory.Services.CreateScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<CaseRegister.Application.Accounts.IPractisingCertificateService>().HasCurrentAsync(user.Id));
    }

    [Fact]
    public async Task Approving_with_a_new_agency_creates_it_even_if_a_similar_one_exists()
    {
        await using var world = await WorldAsync();
        var similar = await world.Factory.CreateAgencyAsync("Copperbelt Homes");

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.Approve(world.Applicant, newAgencyName: "  Copperbelt Homes Ltd  "));

        var user = await world.Fresh();
        Assert.NotEqual(similar.Id, user.AgencyId); // the reviewer chose a new agency, so that is what they got
        var created = await world.Factory.WithDbAsync(db => db.Agencies.AsNoTracking().SingleAsync(agency => agency.Id == user.AgencyId));
        Assert.Equal("Copperbelt Homes Ltd", created.Name);
        Assert.Equal(AgencyStatus.Active, created.Status);
        Assert.Equal(2, await world.Factory.WithDbAsync(db => db.Agencies.CountAsync()));
        Assert.Equal(created.Id, (await world.Application()).ApprovedAgencyId);
    }

    [Fact]
    public async Task The_approved_agent_enrols_in_two_factor_and_can_then_use_what_an_agent_can()
    {
        await using var world = await WorldAsync();
        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));
        var applicant = await world.Fresh();

        var client = world.Factory.CreateApiClient();
        var signIn = await client.SignInAsync(Email, SignUps.StrongPassword); // the password they chose at sign-up
        Assert.Equal("two-factor-enrolment-required", await TwoFactorFlows.StatusAsync(signIn));
        await TwoFactorFlows.EnrolFullyAsync(world.Factory, client, applicant);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/probe/can/RunPreDealCheck")).StatusCode);
        var profile = await (await client.GetAsync("/api/me")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AGT 884", profile.GetProperty("membershipNumber").GetString());
        Assert.True(profile.GetProperty("hasCurrentPractisingCertificate").GetBoolean());
    }

    [Fact]
    public async Task What_the_proposer_chose_is_part_of_the_hash_the_confirmer_must_quote()
    {
        await using var world = await WorldAsync();
        var agency = await world.Factory.CreateAgencyAsync("Copperbelt Homes");

        var existing = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, existingAgencyId: agency.Id));
        await world.Factory.WithdrawAsync(world.A, existing);
        var named = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));
        await world.Factory.WithdrawAsync(world.A, named);
        var renamed = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes Zambia"));

        Assert.Equal(3, new[] { existing.PayloadHash, named.PayloadHash, renamed.PayloadHash }.Distinct().Count());
        Assert.Contains(agency.Id.ToString(), existing.Payload, StringComparison.Ordinal);
        Assert.Contains("\"newAgencyName\":\"Copperbelt Homes Zambia\"", renamed.Payload, StringComparison.Ordinal);
        Assert.Contains("a new agency called \"Copperbelt Homes Zambia\"", renamed.Description, StringComparison.Ordinal);

        // B saw "a new agency called Copperbelt Homes". Quoting that hash does not approve the other one.
        var result = await world.Factory.ConfirmAsync(world.B, renamed, hash: named.PayloadHash);

        Assert.Equal(ConfirmOutcome.HashMismatch, result.Outcome);
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Agencies.CountAsync(a => a.Name.StartsWith("Copperbelt Homes Zambia"))));
    }

    [Fact]
    public async Task An_approval_names_exactly_one_agency_choice()
    {
        await using var world = await WorldAsync();
        var agency = await world.Factory.CreateAgencyAsync("Copperbelt Homes");
        var reason = Proposals.Reason;

        foreach (var bad in new[]
        {
            new ProposeActionCommand(PendingActionType.ApproveApplication, world.Applicant.Id, null, null, null, reason), // neither
            new ProposeActionCommand(PendingActionType.ApproveApplication, world.Applicant.Id, null, null, null, reason, agency.Id, "Also New"), // both
            new ProposeActionCommand(PendingActionType.ApproveApplication, world.Applicant.Id, null, null, null, reason, null, "   "), // a blank name
            new ProposeActionCommand(PendingActionType.ApproveApplication, world.Applicant.Id, null, null, null, reason, null, new string('a', 201)),
            new ProposeActionCommand(PendingActionType.ApproveApplication, world.Applicant.Id, null, Role.Admin, null, reason, agency.Id), // a stray role
            new ProposeActionCommand(PendingActionType.ApproveApplication, null, null, null, null, reason, agency.Id), // no applicant
            new ProposeActionCommand(PendingActionType.ApproveApplication, world.Applicant.Id, null, null, null, reason, agency.Id, null, ["Everything"]), // not a flag
            new ProposeActionCommand(PendingActionType.RejectApplication, world.Applicant.Id, null, null, null, reason, agency.Id), // an approval parameter on a rejection
            new ProposeActionCommand(PendingActionType.ResetTwoFactor, world.Applicant.Id, null, null, null, reason, null, "Sneaky"), // and on anything else
        })
        {
            Assert.Equal(ProposeOutcome.Invalid, (await world.Factory.ProposeAsync(world.A, bad)).Outcome);
        }

        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task An_agency_that_does_not_exist_or_is_suspended_cannot_be_chosen()
    {
        await using var world = await WorldAsync();
        var suspended = await world.Factory.CreateAgencyAsync("Suspended Agency");
        await world.Factory.SuspendAgencyAsync(world.A, suspended.Id);

        var missing = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, existingAgencyId: Guid.NewGuid()));
        var notActive = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, existingAgencyId: suspended.Id));

        Assert.Equal(["AgencyNotFound"], Codes(missing.Problems));
        Assert.Equal(["AgencyNotActive"], Codes(notActive.Problems));
    }

    [Fact]
    public async Task Approval_needs_two_admins_and_only_a_waiting_application_can_be_approved()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));

        Assert.Equal(ConfirmOutcome.NotAllowed, (await world.Factory.ConfirmAsync(world.A, proposal)).Outcome);
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);

        await world.Factory.ConfirmOkAsync(world.B, proposal);

        var again = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));
        Assert.Equal(ProposeOutcome.Refused, again.Outcome);
        Assert.Equal(["TargetStatusChanged"], Codes(again.Problems));
        var notAnApplicant = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.C, newAgencyName: "Whoever"));
        Assert.Equal(ProposeOutcome.Refused, notAnApplicant.Outcome);
        Assert.Equal(["TargetNotFound"], Codes(notAnApplicant.Problems));
    }

    [Fact]
    public async Task Approving_one_applicant_leaves_the_others_waiting()
    {
        await using var world = await WorldAsync();
        var other = await SignUps.ApplyAsync(world.Factory, "other@example.test");

        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));

        Assert.Equal(AccountStatus.Pending, (await world.Factory.GetUserAsync(other.Email!)).AccountStatus);
        Assert.Equal(ApplicationStatus.Pending, (await world.Factory.WithDbAsync(db => db.MemberApplications.AsNoTracking().SingleAsync(a => a.UserId == other.Id))).Status);
    }

    [Fact]
    public async Task Approval_is_audited_with_before_and_after_and_the_effect_is_logged_too()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        var entries = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().ToListAsync());
        var proposed = Assert.Single(entries, e => e.Action == "ActionProposed" && e.Detail!.Contains("ApproveApplication", StringComparison.Ordinal));
        var effect = Assert.Single(entries, e => e.Action == "ApplicationApproved");

        Assert.Contains("before: account: Pending", proposed.Detail, StringComparison.Ordinal);
        Assert.Contains("after: account: Approved", proposed.Detail, StringComparison.Ordinal);
        Assert.Equal(world.B.Id, effect.ActorUserId);
        Assert.Equal(world.Applicant.Id.ToString(), effect.Subject);
        Assert.Contains("new agency Copperbelt Homes", effect.Detail, StringComparison.Ordinal);
    }

    // --- Flags must be acknowledged ------------------------------------------------------------------------------

    private async Task<World> FlaggedWorldAsync()
    {
        var world = await WorldAsync();

        // Someone else is already waiting with the same number: both are flagged, live.
        await SignUps.ApplyAsync(world.Factory, "rival@example.test", SignUps.Body(world.Factory, "rival@example.test", membershipNumber: "AGT 884"));

        return world;
    }

    [Fact]
    public async Task Approving_a_flagged_application_without_acknowledging_the_flag_is_refused()
    {
        await using var world = await FlaggedWorldAsync();

        var result = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));

        Assert.Equal(ProposeOutcome.Refused, result.Outcome);
        Assert.Equal(["FlagNotAcknowledged"], Codes(result.Problems));
        Assert.Contains("DuplicateMembershipNumber", result.Problems![0].Message, StringComparison.Ordinal);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);
    }

    [Fact]
    public async Task Acknowledging_the_flag_in_the_proposal_puts_it_in_front_of_the_confirmer()
    {
        await using var world = await FlaggedWorldAsync();
        // Jane's number is not held by an approved member, only claimed twice, so the approval itself can go ahead.

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, null, "Copperbelt Homes", "DuplicateMembershipNumber"));

        Assert.Contains("\"acknowledgedFlags\":[\"DuplicateMembershipNumber\"]", proposal.Payload, StringComparison.Ordinal);
        Assert.Contains("flags acknowledged: DuplicateMembershipNumber", proposal.Description, StringComparison.Ordinal);
        await world.Factory.ConfirmOkAsync(world.B, proposal);
        Assert.Equal(AccountStatus.Approved, (await world.Fresh()).AccountStatus);
    }

    [Fact]
    public async Task Every_flag_must_be_acknowledged_not_just_one()
    {
        await using var world = await WorldAsync();
        var admins = new[] { world.A, world.B };
        await world.Factory.ProposeAndConfirmAsync(admins[0], admins[1], Proposals.RejectApplication(world.Applicant));
        await world.Factory.CreateUserAsync("holder@example.test", membershipNumber: 884);
        await SignUps.PostAsync(world.Factory.CreateApiClient(), SignUps.Body(world.Factory, Email, membershipNumber: "AGT 884")); // a reapplication that also duplicates a number
        var application = await world.Application();
        Assert.Equal(ApplicationConcerns.Reapplication | ApplicationConcerns.DuplicateMembershipNumber, application.Flags);

        var onlyOne = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, null, "Copperbelt Homes", "Reapplication"));
        var bothButTaken = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, null, "Copperbelt Homes", "Reapplication", "DuplicateMembershipNumber"));

        Assert.Equal(ProposeOutcome.Refused, onlyOne.Outcome);
        Assert.Contains("FlagNotAcknowledged", Codes(onlyOne.Problems));
        Assert.Contains("DuplicateMembershipNumber", onlyOne.Problems![0].Message, StringComparison.Ordinal);

        // With both acknowledged the flags are no obstacle, but the number really is taken by an approved member.
        Assert.Equal(ProposeOutcome.Refused, bothButTaken.Outcome);
        Assert.Equal(["MembershipNumberTaken"], Codes(bothButTaken.Problems));
    }

    [Fact]
    public async Task A_flag_that_arises_after_the_proposal_is_caught_at_confirmation()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes")); // no flags then

        await SignUps.ApplyAsync(world.Factory, "rival@example.test", SignUps.Body(world.Factory, "rival@example.test", membershipNumber: "AGT 884"));
        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal(["FlagNotAcknowledged"], Codes(result.Problems));
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);
        var stored = await world.Factory.GetAsync(proposal.Id);
        Assert.Equal(PendingActionStatus.Pending, stored.Status);
        Assert.Equal("FlagNotAcknowledged", stored.LastRefusalCode);
    }

    [Fact]
    public async Task Acknowledging_a_flag_the_application_does_not_have_does_no_harm()
    {
        await using var world = await WorldAsync();

        var result = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, null, "Copperbelt Homes", "Reapplication"));

        Assert.Equal(ProposeOutcome.Proposed, result.Outcome);
    }

    // --- All or nothing --------------------------------------------------------------------------------------------

    private static async Task BreakRoleAssignmentAsync(ApiFactory factory) =>
        await PostgresFixture.ExecuteAsync(
            factory.OwnerConnectionString!,
            """
            CREATE FUNCTION fail_role_assignment() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated failure assigning a role'; END $$;
            CREATE TRIGGER fail_role_assignment BEFORE INSERT ON user_roles FOR EACH ROW EXECUTE FUNCTION fail_role_assignment();
            """);

    private static async Task RepairRoleAssignmentAsync(ApiFactory factory) =>
        await PostgresFixture.ExecuteAsync(factory.OwnerConnectionString!, "DROP TRIGGER fail_role_assignment ON user_roles; DROP FUNCTION fail_role_assignment();");

    [Fact]
    public async Task When_one_part_of_an_approval_fails_nothing_changes()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Brand New Agency"));
        var rolesBefore = await world.Factory.WithDbAsync(db => db.UserRoles.CountAsync());
        var userBefore = await world.Fresh();

        // The last step, giving the role, fails inside the database after the account, agency and certificate were written.
        await BreakRoleAssignmentAsync(world.Factory);
        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal(["ApplyFailed"], Codes(result.Problems));

        var user = await world.Fresh();
        Assert.Equal(AccountStatus.Pending, user.AccountStatus);
        Assert.Null(user.MembershipNumber);
        Assert.Null(user.MembershipClass);
        Assert.Null(user.AgencyId);
        Assert.Equal(userBefore.SecurityStamp, user.SecurityStamp);
        Assert.Equal(userBefore.ConcurrencyStamp, user.ConcurrencyStamp);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Agencies.CountAsync())); // the new agency was not kept
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PractisingCertificates.CountAsync())); // nor the certificate
        Assert.Equal(rolesBefore, await world.Factory.WithDbAsync(db => db.UserRoles.CountAsync()));
        Assert.Equal(ApplicationStatus.Pending, (await world.Application()).Status);
        Assert.Null((await world.Application()).ApprovedAgencyId);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "ApplicationApproved" || e.Action == "ActionConfirmed")));

        // The proposal is still there, pending, with the reason on it.
        var stored = await world.Factory.GetAsync(proposal.Id);
        Assert.Equal(PendingActionStatus.Pending, stored.Status);
        Assert.Equal("ApplyFailed", stored.LastRefusalCode);

        // Once the fault is gone the very same proposal goes through, whole.
        await RepairRoleAssignmentAsync(world.Factory);
        await world.Factory.ConfirmOkAsync(world.B, proposal);
        Assert.Equal(AccountStatus.Approved, (await world.Fresh()).AccountStatus);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Agencies.CountAsync()));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.PractisingCertificates.CountAsync()));
    }

    [Fact]
    public async Task A_membership_number_taken_in_the_meantime_stops_the_approval_before_anything_is_written()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));
        await world.Factory.CreateUserAsync("late@example.test", membershipNumber: 884);

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Contains("MembershipNumberTaken", Codes(result.Problems));
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Agencies.CountAsync()));
    }

    [Fact]
    public async Task A_certificate_number_recorded_in_the_meantime_stops_the_approval()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));
        var number = (await world.Application()).CertificateNumber;
        var other = await world.Factory.CreateUserAsync("other@example.test");
        await world.Factory.WithDbAsync(async db =>
        {
            db.PractisingCertificates.Add(PractisingCertificate.Create(other.Id, number, new DateOnly(number.Year, 1, 1), new DateOnly(number.Year, 12, 31)));
            await db.SaveChangesAsync();

            return 0;
        });

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal(["CertificateNumberTaken"], Codes(result.Problems));
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);
    }

    [Fact]
    public async Task An_agency_suspended_in_the_meantime_stops_the_approval()
    {
        await using var world = await WorldAsync();
        var agency = await world.Factory.CreateAgencyAsync("Copperbelt Homes");
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, existingAgencyId: agency.Id));
        await world.Factory.SuspendAgencyAsync(world.C, agency.Id);

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal(["AgencyNotActive"], Codes(result.Problems));
        Assert.Null((await world.Fresh()).AgencyId);
    }

    [Fact]
    public async Task A_certificate_whose_year_has_passed_by_confirmation_cannot_be_approved()
    {
        await using var world = await WorldAsync();
        var year = world.Factory.Today.Year;
        world.Factory.Clock.SetUtcNow(new DateTimeOffset(year, 12, 31, 12, 0, 0, TimeSpan.Zero));
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));

        world.Factory.Clock.SetUtcNow(new DateTimeOffset(year, 12, 31, 22, 0, 0, TimeSpan.Zero)); // 1 January in Zambia
        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal(["CertificateYearPassed"], Codes(result.Problems));
    }

    // --- Rejecting ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejecting_needs_two_admins_and_a_reason_and_keeps_the_account()
    {
        await using var world = await WorldAsync();
        var reason = "The certificate number is not on the association's register.";
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.RejectApplication(world.Applicant, reason));
        Assert.Equal(AccountStatus.Pending, (await world.Fresh()).AccountStatus);
        Assert.Equal(ConfirmOutcome.NotAllowed, (await world.Factory.ConfirmAsync(world.A, proposal)).Outcome);

        await world.Factory.ConfirmOkAsync(world.B, proposal);

        var user = await world.Fresh();
        Assert.Equal(AccountStatus.Rejected, user.AccountStatus); // kept, never deleted
        var application = await world.Application();
        Assert.Equal(ApplicationStatus.Rejected, application.Status);
        Assert.Equal(reason, application.DecisionReason);
        Assert.Equal(world.B.Id, application.DecidedByUserId);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Users.CountAsync(u => u.Id == world.Applicant.Id)));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "ApplicationRejected" && e.Subject == world.Applicant.Id.ToString())));
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.CreateApiClient().SignInAsync(Email, SignUps.StrongPassword)).StatusCode);
    }

    [Fact]
    public async Task Rejecting_needs_a_proper_reason()
    {
        await using var world = await WorldAsync();

        var result = await world.Factory.ProposeAsync(world.A, Proposals.RejectApplication(world.Applicant, "no"));

        Assert.Equal(ProposeOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task A_rejected_application_cannot_be_rejected_again_or_approved_and_the_database_never_loses_it()
    {
        await using var world = await WorldAsync();
        await world.Factory.ProposeAndConfirmAsync(world.A, world.B, Proposals.RejectApplication(world.Applicant));

        var again = await world.Factory.ProposeAsync(world.A, Proposals.RejectApplication(world.Applicant));
        var approve = await world.Factory.ProposeAsync(world.A, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"));

        Assert.Equal(["TargetStatusChanged"], Codes(again.Problems));
        Assert.Equal(["TargetStatusChanged"], Codes(approve.Problems));
        Assert.Equal(AccountStatus.Rejected, (await world.Fresh()).AccountStatus);
    }

    [Fact]
    public async Task Rejecting_and_approving_are_one_pending_proposal_each_per_applicant()
    {
        await using var world = await WorldAsync();
        await world.Factory.ProposeOkAsync(world.A, Proposals.RejectApplication(world.Applicant));

        Assert.Equal(ProposeOutcome.AlreadyPending, (await world.Factory.ProposeAsync(world.C, Proposals.RejectApplication(world.Applicant))).Outcome);
        Assert.Equal(ProposeOutcome.Proposed, (await world.Factory.ProposeAsync(world.C, Proposals.Approve(world.Applicant, newAgencyName: "Copperbelt Homes"))).Outcome);
    }

    [Fact]
    public async Task Approving_and_rejecting_over_http_go_through_the_same_two_step()
    {
        await using var world = await WorldAsync();
        var maker = await SignInAsync(world.Factory, world.A);
        var checker = await SignInAsync(world.Factory, world.B);

        var proposed = await maker.SendAsync(
            HttpMethod.Post,
            "/api/admin/pending-actions",
            new
            {
                type = "ApproveApplication",
                targetUserId = world.Applicant.Id,
                newAgencyName = "Copperbelt Homes",
                acknowledgedFlags = Array.Empty<string>(),
                reason = Proposals.Reason,
            });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        var proposal = await proposed.Content.ReadFromJsonAsync<JsonElement>();

        var confirmed = await checker.SendAsync(
            HttpMethod.Post,
            $"/api/admin/pending-actions/{proposal.GetProperty("id").GetGuid()}/confirm",
            new { payloadHash = proposal.GetProperty("payloadHash").GetString(), twoFactorCode = world.Factory.NextTotpCode(world.B) });

        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(AccountStatus.Approved, (await world.Fresh()).AccountStatus);
    }
}
