using CaseRegister.Application.Accounts;
using CaseRegister.Application.Approvals;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Approvals;
using CaseRegister.Infrastructure.Identity;
using CaseRegister.Infrastructure.Persistence;
using CaseRegister.Tests.Database;
using CaseRegister.Tests.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaseRegister.Tests.Auth;

/// <summary>The approval engine: one admin proposes, a different admin confirms, and nothing happens in between.</summary>
[Collection(PostgresTestGroup.Name)]
public class MakerCheckerTests(PostgresFixture postgres)
{
    private sealed record World(
        ApiFactory Factory,
        ApplicationUser A,
        ApplicationUser B,
        ApplicationUser C,
        Agency Agency,
        ApplicationUser Member,
        ApplicationUser Staff) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Factory.DisposeAsync();

        public Task<IReadOnlyList<Role>> RolesOf(ApplicationUser user) => Factory.RolesOfAsync(user);

        public Task<int> AuditCount(string action) => Factory.WithDbAsync(db => db.AuditLog.CountAsync(entry => entry.Action == action));
    }

    /// <summary>Three admins (A proposes, B and C confirm), a member who is an agent, and a staff account with no role yet.</summary>
    private async Task<World> WorldAsync(Action<Dictionary<string, string?>>? settings = null, Action<IServiceCollection>? configureServices = null)
    {
        var factory = await ApiFactory.CreateAsync(postgres, settings, configureServices: configureServices);
        var agency = await factory.CreateAgencyAsync();
        var admins = await factory.CreateAdminsAsync(3);
        var member = await factory.CreateMemberAsync("member@example.test", agency, [Role.RegisteredAgent]);
        var staff = await factory.CreateUserAsync("staff@example.test", staff: true);

        return new World(factory, admins[0], admins[1], admins[2], agency, member, staff);
    }

    private static string[] Codes(IEnumerable<ApprovalViolation>? problems) => [.. (problems ?? []).Select(problem => problem.Code).Order(StringComparer.Ordinal)];

    // --- The basic two-step ---------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_changes_when_a_role_is_proposed_and_it_is_applied_only_when_a_different_admin_confirms()
    {
        await using var world = await WorldAsync();

        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        Assert.Equal(PendingActionStatus.Pending, proposal.Status);
        Assert.Equal(world.A.Id, proposal.ProposedByUserId);
        Assert.Empty(await world.RolesOf(world.Staff)); // proposing changes nothing

        var confirmed = await world.Factory.ConfirmOkAsync(world.B, proposal);

        Assert.Equal(PendingActionStatus.Confirmed, confirmed.Status);
        Assert.Equal(world.B.Id, confirmed.DecidedByUserId);
        Assert.NotNull(confirmed.DecidedAt);
        Assert.Equal([Role.CaseReviewer], await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task The_proposer_cannot_confirm_their_own_proposal()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var result = await world.Factory.ConfirmAsync(world.A, proposal);

        Assert.Equal(ConfirmOutcome.NotAllowed, result.Outcome);
        Assert.Contains(ApprovalRules.SameAsProposer, Codes(result.Problems));
        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(proposal.Id)).Status);
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task A_proposal_that_targets_the_proposer_is_refused_and_nothing_is_stored()
    {
        await using var world = await WorldAsync();

        foreach (var command in new[]
        {
            Proposals.ForUser(PendingActionType.ResetTwoFactor, world.A),
            Proposals.AssignRole(world.A, Role.CaseReviewer),
            Proposals.RemoveRole(world.A, Role.Admin),
            Proposals.ForUser(PendingActionType.ReactivateUser, world.A),
        })
        {
            var result = await world.Factory.ProposeAsync(world.A, command);

            Assert.Equal(ProposeOutcome.Refused, result.Outcome);
            Assert.Equal([ApprovalRules.SelfTarget], Codes(result.Problems));
        }

        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task Nobody_can_confirm_an_action_that_targets_them_but_another_admin_can()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ResetTwoFactor, world.B));

        var byTheTarget = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.NotAllowed, byTheTarget.Outcome);
        Assert.Equal([ApprovalRules.SelfTarget], Codes(byTheTarget.Problems));
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.Authenticators.IgnoreQueryFilters().CountAsync(a => a.UserId == world.B.Id && a.RemovedAt == null)));

        await world.Factory.ConfirmOkAsync(world.C, proposal);

        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.Authenticators.CountAsync(a => a.UserId == world.B.Id)));
    }

    [Fact]
    public async Task Rejecting_a_proposal_that_targets_you_is_refused_too()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ResetTwoFactor, world.B));

        var result = await world.Factory.RejectAsync(world.B, proposal);

        Assert.Equal(DecisionOutcome.NotAllowed, result.Outcome);
        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(proposal.Id)).Status);
    }

    [Fact]
    public async Task Only_admins_can_propose_or_confirm_whatever_else_they_are()
    {
        await using var world = await WorldAsync();
        var reviewer = await world.Factory.CreateStaffAsync("reviewer@example.test", Role.CaseReviewer);
        var suspendedAdmin = await world.Factory.CreateStaffAsync("suspended@example.test", Role.Admin);
        await world.Factory.WithDbAsync(async db =>
        {
            // Still holds the Admin role, but is no longer active (for example suspended by the lockout rule).
            (await db.Users.SingleAsync(u => u.Id == suspendedAdmin.Id)).AccountStatus = AccountStatus.Suspended;
            await db.SaveChangesAsync();

            return 0;
        });
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        foreach (var notAdmin in new[] { world.Member, reviewer, world.Staff, suspendedAdmin })
        {
            Assert.Equal(ProposeOutcome.NotAnAdmin, (await world.Factory.ProposeAsync(notAdmin, Proposals.AssignRole(world.Staff, Role.CaseReviewer))).Outcome);
            Assert.Equal(ConfirmOutcome.NotAnAdmin, (await world.Factory.ConfirmAsync(notAdmin, proposal)).Outcome);
            Assert.Equal(DecisionOutcome.NotAnAdmin, (await world.Factory.RejectAsync(notAdmin, proposal)).Outcome);
            Assert.Equal(DecisionOutcome.NotAnAdmin, (await world.Factory.WithdrawAsync(notAdmin, proposal)).Outcome);
        }

        Assert.Empty(await world.RolesOf(world.Staff));
    }

    // --- One pending proposal per target and type ------------------------------------------------------------

    [Fact]
    public async Task Only_one_proposal_can_be_pending_for_a_target_and_action_type()
    {
        await using var world = await WorldAsync();
        var first = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        Assert.Equal(ProposeOutcome.AlreadyPending, (await world.Factory.ProposeAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer))).Outcome);
        Assert.Equal(ProposeOutcome.AlreadyPending, (await world.Factory.ProposeAsync(world.C, Proposals.AssignRole(world.Staff, Role.CaseReviewer))).Outcome);

        // Even for a different role: it is the same type and the same target.
        Assert.Equal(ProposeOutcome.AlreadyPending, (await world.Factory.ProposeAsync(world.C, Proposals.AssignRole(world.Staff, Role.Admin))).Outcome);

        // A different type, or a different target, is a different proposal.
        await world.Factory.ProposeOkAsync(world.C, Proposals.ForUser(PendingActionType.ResetTwoFactor, world.Staff));
        await world.Factory.ProposeOkAsync(world.C, Proposals.AssignRole(world.Member, Role.AgencyComplianceOfficer));

        Assert.Equal(3, (await world.Factory.ListAsync()).Count);
        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(first.Id)).Status);
    }

    [Fact]
    public async Task The_same_thing_can_be_proposed_again_once_the_earlier_proposal_is_decided()
    {
        await using var world = await WorldAsync();
        var command = Proposals.AssignRole(world.Staff, Role.CaseReviewer);

        var rejected = await world.Factory.ProposeOkAsync(world.A, command);
        await world.Factory.RejectAsync(world.B, rejected);
        var withdrawn = await world.Factory.ProposeOkAsync(world.A, command);
        await world.Factory.WithdrawAsync(world.A, withdrawn);
        var confirmed = await world.Factory.ProposeOkAsync(world.A, command);
        await world.Factory.ConfirmOkAsync(world.B, confirmed);

        // Now there is nothing to do (the role is held), which is a different refusal, not "already pending".
        var again = await world.Factory.ProposeAsync(world.A, command);
        Assert.Equal(ProposeOutcome.Refused, again.Outcome);
        Assert.Contains("NothingToChange", Codes(again.Problems));
        Assert.Equal(3, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    [Fact]
    public async Task Two_admins_proposing_the_same_thing_at_the_same_moment_make_exactly_one_proposal()
    {
        await using var world = await WorldAsync();

        for (var round = 0; round < 4; round++)
        {
            var target = await world.Factory.CreateUserAsync($"target{round}@example.test", staff: true);
            var gate = new TaskCompletionSource();
            Task<ProposeResult> Propose(ApplicationUser by) => Task.Run(async () =>
            {
                await gate.Task;
                return await world.Factory.ProposeAsync(by, Proposals.AssignRole(target, Role.CaseReviewer));
            });

            var first = Propose(world.A);
            var second = Propose(world.C);
            gate.SetResult();
            var outcomes = (await Task.WhenAll(first, second)).Select(result => result.Outcome).Order().ToArray();

            Assert.Equal([ProposeOutcome.Proposed, ProposeOutcome.AlreadyPending], outcomes);
            Assert.Equal(1, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync(a => a.TargetUserId == target.Id)));
        }
    }

    // --- Expiry ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_proposal_can_still_be_confirmed_one_second_before_it_expires()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        Assert.Equal(proposal.ProposedAt.AddHours(72), proposal.ExpiresAt);

        world.Factory.Clock.Advance(TimeSpan.FromHours(72) - TimeSpan.FromSeconds(1));

        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(proposal.Id)).Status);
        await world.Factory.ConfirmOkAsync(world.B, proposal);
        Assert.Equal([Role.CaseReviewer], await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task A_proposal_cannot_be_confirmed_once_seventy_two_hours_have_passed()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        world.Factory.Clock.Advance(TimeSpan.FromHours(72));
        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Expired, result.Outcome);
        Assert.Empty(await world.RolesOf(world.Staff));
        Assert.Equal(PendingActionStatus.Expired, (await world.Factory.GetAsync(proposal.Id)).Status);
    }

    [Fact]
    public async Task Expiry_is_worked_out_when_read_and_written_down_the_next_time_the_proposal_is_touched()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        world.Factory.Clock.Advance(TimeSpan.FromHours(100));

        // Looking does not write anything: the row still says Pending, but it is shown as expired and listed no more.
        Assert.Empty(await world.Factory.ListAsync());
        var closed = Assert.Single(await world.Factory.ListAsync(includeClosed: true));
        Assert.Equal(PendingActionStatus.Expired, closed.Status);
        Assert.Equal("Pending", await world.Factory.WithDbAsync(db => db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM pending_actions").SingleAsync()));
        Assert.Equal(0, await world.AuditCount("ActionExpired"));

        // Touching it writes it down, dated at the moment it lapsed, and audits it.
        await world.Factory.ConfirmAsync(world.B, proposal);

        var stored = await world.Factory.WithDbAsync(db => db.PendingActions.AsNoTracking().SingleAsync());
        Assert.Equal(PendingActionStatus.Expired, stored.Status);
        Assert.Equal(proposal.ExpiresAt, stored.DecidedAt);
        Assert.Equal(1, await world.AuditCount("ActionExpired"));
    }

    [Fact]
    public async Task An_expired_proposal_does_not_block_a_new_one_and_is_marked_expired_by_it()
    {
        await using var world = await WorldAsync();
        var command = Proposals.AssignRole(world.Staff, Role.CaseReviewer);
        var old = await world.Factory.ProposeOkAsync(world.A, command);
        world.Factory.Clock.Advance(TimeSpan.FromHours(73));

        var fresh = await world.Factory.ProposeOkAsync(world.A, command);

        Assert.NotEqual(old.Id, fresh.Id);
        Assert.Equal(PendingActionStatus.Expired, (await world.Factory.GetAsync(old.Id)).Status);
        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(fresh.Id)).Status);
        Assert.Equal(ConfirmOutcome.Expired, (await world.Factory.ConfirmAsync(world.B, old)).Outcome);
        await world.Factory.ConfirmOkAsync(world.B, fresh);
    }

    [Fact]
    public async Task An_expired_proposal_cannot_be_rejected_or_withdrawn()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        world.Factory.Clock.Advance(TimeSpan.FromHours(72));

        Assert.Equal(DecisionOutcome.Expired, (await world.Factory.RejectAsync(world.B, proposal)).Outcome);
        Assert.Equal(DecisionOutcome.Expired, (await world.Factory.WithdrawAsync(world.A, proposal)).Outcome);
    }

    [Fact]
    public async Task The_list_shows_what_is_waiting_with_its_expiry_and_the_hash_to_quote()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Member, Role.AgencyComplianceOfficer));

        var listed = Assert.Single(await world.Factory.ListAsync());

        Assert.Equal(proposal.Id, listed.Id);
        Assert.Equal(PendingActionType.AssignRole, listed.Type);
        Assert.Equal(world.Member.Id, listed.TargetUserId);
        Assert.Equal(world.Member.FullName, listed.TargetName);
        Assert.Equal("Give Test Person the Agency compliance officer role", listed.Description);
        Assert.Equal("{\"role\":\"AgencyComplianceOfficer\"}", listed.Payload);
        Assert.Equal(64, listed.PayloadHash.Length);
        Assert.Equal(Proposals.Reason, listed.Reason);
        Assert.Equal(world.A.Id, listed.ProposedByUserId);
        Assert.Equal(listed.ProposedAt.AddHours(72), listed.ExpiresAt);
        Assert.Equal(PendingActionStatus.Pending, listed.Status);
    }

    // --- Withdrawing and rejecting -----------------------------------------------------------------------------

    [Fact]
    public async Task The_proposer_can_withdraw_and_then_nobody_can_confirm_it()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var withdrawn = await world.Factory.WithdrawAsync(world.A, proposal, "Proposed for the wrong person.");

        Assert.Equal(DecisionOutcome.Done, withdrawn.Outcome);
        Assert.Equal(PendingActionStatus.Withdrawn, withdrawn.Action!.Status);
        Assert.Equal("Proposed for the wrong person.", withdrawn.Action.DecisionReason);
        Assert.Equal(world.A.Id, withdrawn.Action.DecidedByUserId);
        Assert.Equal(ConfirmOutcome.NotPending, (await world.Factory.ConfirmAsync(world.B, proposal)).Outcome);
        Assert.Empty(await world.RolesOf(world.Staff));
        Assert.Empty(await world.Factory.ListAsync());
    }

    [Fact]
    public async Task Only_the_proposer_can_withdraw()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var result = await world.Factory.WithdrawAsync(world.B, proposal);

        Assert.Equal(DecisionOutcome.NotAllowed, result.Outcome);
        Assert.Contains("NotProposer", Codes(result.Problems));
        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(proposal.Id)).Status);
    }

    [Fact]
    public async Task A_different_admin_can_reject_and_the_proposer_cannot()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        Assert.Equal(DecisionOutcome.NotAllowed, (await world.Factory.RejectAsync(world.A, proposal)).Outcome);

        var rejected = await world.Factory.RejectAsync(world.B, proposal, "This person does not need that access.");

        Assert.Equal(DecisionOutcome.Done, rejected.Outcome);
        Assert.Equal(PendingActionStatus.Rejected, rejected.Action!.Status);
        Assert.Equal("This person does not need that access.", rejected.Action.DecisionReason);
        Assert.Equal(world.B.Id, rejected.Action.DecidedByUserId);
        Assert.Equal(ConfirmOutcome.NotPending, (await world.Factory.ConfirmAsync(world.C, proposal)).Outcome);
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too short")]
    public async Task Rejecting_and_withdrawing_need_a_proper_reason(string reason)
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        Assert.Equal(DecisionOutcome.Invalid, (await world.Factory.RejectAsync(world.B, proposal, reason)).Outcome);
        Assert.Equal(DecisionOutcome.Invalid, (await world.Factory.WithdrawAsync(world.A, proposal, reason)).Outcome);
        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(proposal.Id)).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too short")]
    public async Task Proposing_needs_a_proper_reason(string reason)
    {
        await using var world = await WorldAsync();

        var result = await world.Factory.ProposeAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer) with { Reason = reason });

        Assert.Equal(ProposeOutcome.Invalid, result.Outcome);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.PendingActions.CountAsync()));
    }

    // --- The hash: the confirmer approves exactly what they saw ------------------------------------------------

    [Fact]
    public async Task A_confirmation_quoting_the_wrong_hash_is_refused_and_nothing_is_applied()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var result = await world.Factory.ConfirmAsync(world.B, proposal, hash: new string('0', 64));

        Assert.Equal(ConfirmOutcome.HashMismatch, result.Outcome);
        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(proposal.Id)).Status);
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task The_hash_of_a_different_proposal_does_not_confirm_this_one()
    {
        await using var world = await WorldAsync();
        var harmless = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.WithdrawAsync(world.A, harmless);
        var powerful = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.Admin));

        Assert.NotEqual(harmless.PayloadHash, powerful.PayloadHash);

        // What B was shown was the first one. The second must not go through on its hash.
        var result = await world.Factory.ConfirmAsync(world.B, powerful, hash: harmless.PayloadHash);

        Assert.Equal(ConfirmOutcome.HashMismatch, result.Outcome);
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task A_hash_with_different_capitals_or_spaces_is_still_the_same_hash()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var result = await world.Factory.ConfirmAsync(world.B, proposal, hash: " " + proposal.PayloadHash.ToUpperInvariant() + " ");

        Assert.Equal(ConfirmOutcome.Confirmed, result.Outcome);
    }

    [Fact]
    public async Task A_proposal_altered_behind_the_applications_back_is_not_applied()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        // The database owner (not the application, which cannot) changes the role in the stored proposal.
        await PostgresFixture.ExecuteAsync(
            world.Factory.OwnerConnectionString!,
            $"UPDATE pending_actions SET payload = '{{\"role\":\"Admin\"}}' WHERE id = '{proposal.Id}'");

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Contains("ProposalTampered", Codes(result.Problems));
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    // --- Checked again at confirmation -------------------------------------------------------------------------

    [Fact]
    public async Task A_rule_that_breaks_between_proposal_and_confirmation_refuses_it_and_leaves_it_pending_with_the_reason()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Member, Role.AgencyComplianceOfficer));

        // After the proposal the member stops being a registered agent, so they can no longer be a compliance officer.
        Assert.True((await world.Factory.TryChangeRolesAsync(new RoleChange(world.Member.Id, [], [Role.RegisteredAgent]))).Succeeded);

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal([RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent], Codes(result.Problems));
        Assert.Empty(await world.RolesOf(world.Member)); // nothing applied

        var stored = await world.Factory.GetAsync(proposal.Id);
        Assert.Equal(PendingActionStatus.Pending, stored.Status); // still waiting
        Assert.Equal(RoleAssignmentRules.ComplianceOfficerMustBeRegisteredAgent, stored.LastRefusalCode);
        Assert.NotNull(stored.LastRefusalMessage);
        Assert.NotNull(stored.LastRefusedAt);
        Assert.Equal(1, await world.AuditCount("ActionRefused"));

        // Put the situation right, and the same proposal can still be confirmed.
        await world.Factory.AssignRolesAsync(world.Member, Role.RegisteredAgent);
        await world.Factory.ConfirmOkAsync(world.B, proposal);
        Assert.Equal([Role.RegisteredAgent, Role.AgencyComplianceOfficer], await world.RolesOf(world.Member));
    }

    [Fact]
    public async Task A_refusal_at_confirmation_does_not_use_up_the_confirmers_code()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Member, Role.AgencyComplianceOfficer));
        await world.Factory.TryChangeRolesAsync(new RoleChange(world.Member.Id, [], [Role.RegisteredAgent]));
        var code = world.Factory.CodeForStep(world.B, world.Factory.CurrentStep);

        Assert.Equal(ConfirmOutcome.Refused, (await world.Factory.ConfirmAsync(world.B, proposal, code: code)).Outcome);
        await world.Factory.AssignRolesAsync(world.Member, Role.RegisteredAgent);

        // The very same code, in the very same time step, still works: the refusal came before it was checked.
        Assert.Equal(ConfirmOutcome.Confirmed, (await world.Factory.ConfirmAsync(world.B, proposal, code: code)).Outcome);
    }

    [Fact]
    public async Task A_target_that_was_suspended_in_the_meantime_cannot_be_given_a_role()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        Assert.Equal(AdminActionOutcome.Done, (await world.Factory.SuspendUserAsync(world.C, world.Staff)).Outcome);

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Equal(["TargetStatusChanged"], Codes(result.Problems));
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task A_proposer_who_is_no_longer_an_active_admin_leaves_a_proposal_that_cannot_be_confirmed()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.WithDbAsync(async db =>
        {
            // For example locked out repeatedly and suspended by the lockout rule.
            var user = await db.Users.SingleAsync(u => u.Id == world.A.Id);
            user.AccountStatus = AccountStatus.Suspended;
            await db.SaveChangesAsync();

            return 0;
        });

        var result = await world.Factory.ConfirmAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.Refused, result.Outcome);
        Assert.Contains("ProposerNoLongerAdmin", Codes(result.Problems));
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    // --- A fresh two-factor code ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_wrong_code_does_not_confirm_and_leaves_the_proposal_pending()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        foreach (var wrong in new[] { "000000", "12345", "abcdef", "  " })
        {
            Assert.Equal(ConfirmOutcome.CodeRejected, (await world.Factory.ConfirmAsync(world.B, proposal, code: wrong)).Outcome);
        }

        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(proposal.Id)).Status);
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task Another_admins_code_does_not_confirm()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var result = await world.Factory.ConfirmAsync(world.B, proposal, code: world.Factory.NextTotpCode(world.C));

        Assert.Equal(ConfirmOutcome.CodeRejected, result.Outcome);
        Assert.Empty(await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task A_code_that_was_already_used_cannot_confirm()
    {
        await using var world = await WorldAsync();
        var first = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        var second = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Member, Role.AgencyComplianceOfficer));
        await world.Factory.ConfirmOkAsync(world.B, first); // spends the code for the current time step...

        // ...so the same code again, or one from the step before, is a replay.
        foreach (var step in new[] { world.Factory.CurrentStep, world.Factory.CurrentStep - 1 })
        {
            var replay = await world.Factory.ConfirmAsync(world.B, second, code: world.Factory.CodeForStep(world.B, step));

            Assert.Equal(ConfirmOutcome.CodeRejected, replay.Outcome);
        }

        Assert.Equal(PendingActionStatus.Pending, (await world.Factory.GetAsync(second.Id)).Status);
        Assert.Equal(ConfirmOutcome.Confirmed, (await world.Factory.ConfirmAsync(world.B, second)).Outcome); // a fresh code works
    }

    [Fact]
    public async Task An_admin_with_no_two_factor_set_up_cannot_confirm()
    {
        await using var world = await WorldAsync();
        var bare = await world.Factory.CreateUserAsync("bare@example.test", staff: true, twoFactor: false);
        await world.Factory.AssignRolesAsync(bare, Role.Admin);
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));

        var result = await world.Factory.ConfirmAsync(bare, proposal, code: "123456");

        Assert.Equal(ConfirmOutcome.CodeRejected, result.Outcome);
    }

    // --- Confirming twice, and at the same moment -----------------------------------------------------------------

    [Fact]
    public async Task A_repeated_confirmation_gets_the_same_result_with_no_second_effect()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        var first = await world.Factory.ConfirmAsync(world.B, proposal);
        var stampAfterFirst = (await world.Factory.GetUserAsync(world.Staff.Email!)).SecurityStamp;
        var auditAfterFirst = await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync());

        // Even with a code that is no good, because it is not asked for: nothing is being done.
        var again = await world.Factory.ConfirmAsync(world.B, proposal, code: "000000");

        Assert.Equal(ConfirmOutcome.Confirmed, first.Outcome);
        Assert.False(first.WasRepeat);
        Assert.Equal(ConfirmOutcome.Confirmed, again.Outcome);
        Assert.True(again.WasRepeat);
        Assert.Equal(first.Action, again.Action); // the same result
        Assert.Equal(stampAfterFirst, (await world.Factory.GetUserAsync(world.Staff.Email!)).SecurityStamp); // no second effect
        Assert.Equal(auditAfterFirst, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync()));
        Assert.Equal([Role.CaseReviewer], await world.RolesOf(world.Staff));
    }

    [Fact]
    public async Task A_repeat_still_needs_the_right_hash_and_the_same_admin()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        Assert.Equal(ConfirmOutcome.HashMismatch, (await world.Factory.ConfirmAsync(world.B, proposal, hash: new string('1', 64))).Outcome);
        Assert.Equal(ConfirmOutcome.NotPending, (await world.Factory.ConfirmAsync(world.C, proposal)).Outcome); // someone else: already decided
    }

    /// <summary>
    /// Holds one admin's confirmation at the moment it looks at the state of the target, so a test can make the other admin's
    /// confirmation go through first, in exactly the order a slow machine produces by chance.
    /// </summary>
    internal sealed class Pause(int onCall)
    {
        private int _calls;

        public Guid Admin { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitIfThisIsTheMomentAsync(Guid actor, CancellationToken cancellationToken)
        {
            if (actor == Admin && Interlocked.Increment(ref _calls) == onCall)
            {
                Reached.SetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            }
        }
    }

    /// <summary>The real operation, with a look at the shared pause before it plans (each request has its own copy of the operation; the pause is one).</summary>
    internal sealed class PausingOperation(IAdminOperation inner, Pause pause) : IAdminOperation
    {
        public PendingActionType Type => inner.Type;

        public async Task<OperationPlan> PlanAsync(OperationContext context, CancellationToken cancellationToken)
        {
            await pause.WaitIfThisIsTheMomentAsync(context.ActorId, cancellationToken);

            return await inner.PlanAsync(context, cancellationToken);
        }
    }

    /// <summary>Swaps the reactivate operation for one that can be held at the chosen admin's chosen look at the state.</summary>
    private static Action<IServiceCollection> PauseReactivation(Pause pause) => services =>
    {
        var original = services.Single(descriptor => descriptor.ImplementationType == typeof(ReactivateUserOperation));
        services.Remove(original);
        services.AddScoped<IAdminOperation>(provider => new PausingOperation(ActivatorUtilities.CreateInstance<ReactivateUserOperation>(provider), pause));
    };

    [Fact]
    public async Task A_confirmation_that_looks_at_the_state_after_another_one_went_through_is_not_pending_and_never_a_refusal()
    {
        // C has read the proposal as pending and is about to check the state of the account. B confirms completely first.
        // The account is now active, so the state looks wrong to C: the answer is still "not pending", not a refusal.
        var pause = new Pause(onCall: 1);
        await using var world = await WorldAsync(configureServices: PauseReactivation(pause));
        pause.Admin = world.C.Id;
        var target = await world.Factory.CreateUserAsync("held@example.test", status: AccountStatus.Suspended);
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ReactivateUser, target));
        var codeB = world.Factory.NextTotpCode(world.B);
        var codeC = world.Factory.NextTotpCode(world.C);

        var loser = Task.Run(async () =>
        {
            using var scope = world.Factory.Services.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<IPendingActionService>().ConfirmAsync(proposal.Id, world.C.Id, new ConfirmActionCommand(proposal.PayloadHash, codeC), null);
        });
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var winner = await world.Factory.ConfirmAsync(world.B, proposal, code: codeB);
        pause.Release.SetResult();
        var result = await loser;

        Assert.Equal(ConfirmOutcome.Confirmed, winner.Outcome);
        Assert.Equal(ConfirmOutcome.NotPending, result.Outcome);
        Assert.Contains(result.Problems!, problem => problem.Code == ApprovalRules.NotPending);
        Assert.Equal(0, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "ActionRefused" && e.Detail!.StartsWith(proposal.Id.ToString()))));
        Assert.Null((await world.Factory.WithDbAsync(db => db.PendingActions.AsNoTracking().SingleAsync(a => a.Id == proposal.Id))).LastRefusalCode);
    }

    [Fact]
    public async Task A_confirmation_that_is_deciding_makes_the_other_one_wait_and_then_find_the_proposal_decided()
    {
        // C is inside the decision, holding it. B arrives, has to wait for C, and so finds it already decided.
        var pause = new Pause(onCall: 2);
        await using var world = await WorldAsync(configureServices: PauseReactivation(pause));
        pause.Admin = world.C.Id;
        var target = await world.Factory.CreateUserAsync("held2@example.test", status: AccountStatus.Suspended);
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ReactivateUser, target));
        var codeB = world.Factory.NextTotpCode(world.B);
        var codeC = world.Factory.NextTotpCode(world.C);

        var deciding = Task.Run(async () =>
        {
            using var scope = world.Factory.Services.CreateScope();

            return await scope.ServiceProvider.GetRequiredService<IPendingActionService>().ConfirmAsync(proposal.Id, world.C.Id, new ConfirmActionCommand(proposal.PayloadHash, codeC), null);
        });
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30)); // C holds the proposal, in the middle of deciding it
        var waiting = Task.Run(async () => await world.Factory.ConfirmAsync(world.B, proposal, code: codeB));
        await Task.Delay(TimeSpan.FromSeconds(1)); // long enough for B to get as far as waiting for C
        Assert.False(waiting.IsCompleted, "B must wait for C instead of deciding alongside it");
        pause.Release.SetResult();

        var results = await Task.WhenAll(deciding, waiting);

        Assert.Equal(ConfirmOutcome.Confirmed, results[0].Outcome);
        Assert.Equal(ConfirmOutcome.NotPending, results[1].Outcome);
        Assert.Equal(world.C.Id, (await world.Factory.WithDbAsync(db => db.PendingActions.AsNoTracking().SingleAsync(a => a.Id == proposal.Id))).DecidedByUserId);
        Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "UserReactivated" && e.Subject == target.Id.ToString())));
    }

    [Fact]
    public async Task Two_simultaneous_confirmations_apply_the_action_exactly_once()
    {
        await using var world = await WorldAsync();

        // A stress test: the same race, fifty times, with the exact outcomes asserted every time. The loser has to be "not pending"
        // however the two requests happen to interleave, never a refusal about a state the winner has just changed.
        for (var round = 0; round < 50; round++)
        {
            var target = await world.Factory.CreateUserAsync($"suspended{round}@example.test", status: AccountStatus.Suspended);
            var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.ForUser(PendingActionType.ReactivateUser, target));
            var codeB = world.Factory.NextTotpCode(world.B);
            var codeC = world.Factory.NextTotpCode(world.C);
            var gate = new TaskCompletionSource();

            Task<ConfirmResult> Confirm(ApplicationUser by, string code) => Task.Run(async () =>
            {
                await gate.Task;
                using var scope = world.Factory.Services.CreateScope();

                return await scope.ServiceProvider.GetRequiredService<IPendingActionService>()
                    .ConfirmAsync(proposal.Id, by.Id, new ConfirmActionCommand(proposal.PayloadHash, code), null);
            });

            var byB = Confirm(world.B, codeB);
            var byC = Confirm(world.C, codeC);
            gate.SetResult();
            var results = await Task.WhenAll(byB, byC);

            var outcomes = results.Select(result => result.Outcome).Order().ToList();
            Assert.True(outcomes.SequenceEqual([ConfirmOutcome.Confirmed, ConfirmOutcome.NotPending]), $"round {round}: {string.Join(", ", outcomes)}");
            Assert.False(results.Single(result => result.Outcome == ConfirmOutcome.Confirmed).WasRepeat, $"round {round}");
            Assert.Contains(results.Single(result => result.Outcome == ConfirmOutcome.NotPending).Problems!, problem => problem.Code == ApprovalRules.NotPending);

            // One decision, one effect, one set of audit entries.
            var stored = await world.Factory.WithDbAsync(db => db.PendingActions.AsNoTracking().SingleAsync(a => a.Id == proposal.Id));
            Assert.Equal(PendingActionStatus.Confirmed, stored.Status);
            Assert.Equal(AccountStatus.Approved, (await world.Factory.GetUserAsync(target.Email!)).AccountStatus);
            Assert.Null(stored.LastRefusalCode);
            Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "UserReactivated" && e.Subject == target.Id.ToString())));
            Assert.Equal(1, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "ActionConfirmed" && e.Detail!.StartsWith(proposal.Id.ToString()))));

            // The loser was told "not pending" and nothing was written down as a refusal, and the proposal carries no refusal note.
            Assert.Equal(0, await world.Factory.WithDbAsync(db => db.AuditLog.CountAsync(e => e.Action == "ActionRefused" && e.Detail!.StartsWith(proposal.Id.ToString()))));
        }
    }

    [Fact]
    public async Task The_database_lets_only_one_of_two_stale_copies_of_a_proposal_be_decided()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        using var firstScope = world.Factory.Services.CreateScope();
        using var secondScope = world.Factory.Services.CreateScope();
        var first = firstScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var copyOne = await first.PendingActions.SingleAsync(a => a.Id == proposal.Id);
        var copyTwo = await second.PendingActions.SingleAsync(a => a.Id == proposal.Id);
        var now = world.Factory.Clock.GetUtcNow();

        copyOne.Confirm(world.B.Id, now);
        await first.SaveChangesAsync();
        copyTwo.Reject(world.C.Id, now, "A different decision at the same moment.");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    // --- Audit --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Both_steps_are_audited_with_before_and_after_values()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Member, Role.AgencyComplianceOfficer));
        await world.Factory.ConfirmOkAsync(world.B, proposal);

        var entries = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync());
        var proposed = Assert.Single(entries, e => e.Action == "ActionProposed");
        var confirmed = Assert.Single(entries, e => e.Action == "ActionConfirmed");
        var effect = entries.Last(e => e.Action == "RolesChanged");

        Assert.Equal(world.A.Id, proposed.ActorUserId);
        Assert.Equal(world.Member.Id.ToString(), proposed.Subject);
        Assert.Contains("before: roles: RegisteredAgent", proposed.Detail, StringComparison.Ordinal);
        Assert.Contains("after: roles: RegisteredAgent, AgencyComplianceOfficer", proposed.Detail, StringComparison.Ordinal);
        Assert.Contains(proposal.Id.ToString(), proposed.Detail, StringComparison.Ordinal);
        Assert.Contains(Proposals.Reason, proposed.Detail, StringComparison.Ordinal);

        Assert.Equal(world.B.Id, confirmed.ActorUserId);
        Assert.Equal(world.Member.Id.ToString(), confirmed.Subject);
        Assert.Contains("before: roles: RegisteredAgent", confirmed.Detail, StringComparison.Ordinal);
        Assert.Contains("after: roles: RegisteredAgent, AgencyComplianceOfficer", confirmed.Detail, StringComparison.Ordinal);
        Assert.Contains($"proposed by {world.A.Id}", confirmed.Detail, StringComparison.Ordinal);

        Assert.Equal(world.B.Id, effect.ActorUserId);
        Assert.Contains(proposal.Id.ToString(), effect.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejecting_and_withdrawing_are_audited_and_say_that_nothing_was_applied()
    {
        await using var world = await WorldAsync();
        var one = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.RejectAsync(world.B, one, "No need for this access any more.");
        var two = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        await world.Factory.WithdrawAsync(world.A, two, "Changed my mind about this one.");

        var entries = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().ToListAsync());
        var rejected = Assert.Single(entries, e => e.Action == "ActionRejected");
        var withdrawn = Assert.Single(entries, e => e.Action == "ActionWithdrawn");

        Assert.Equal(world.B.Id, rejected.ActorUserId);
        Assert.Contains("status: Pending -> status: Rejected; nothing was applied", rejected.Detail, StringComparison.Ordinal);
        Assert.Contains("No need for this access any more.", rejected.Detail, StringComparison.Ordinal);
        Assert.Equal(world.A.Id, withdrawn.ActorUserId);
        Assert.Contains("status: Pending -> status: Withdrawn; nothing was applied", withdrawn.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_audit_log_never_holds_a_two_factor_code()
    {
        await using var world = await WorldAsync();
        var proposal = await world.Factory.ProposeOkAsync(world.A, Proposals.AssignRole(world.Staff, Role.CaseReviewer));
        var code = world.Factory.NextTotpCode(world.B);

        await world.Factory.ConfirmAsync(world.B, proposal, code: code);

        var all = await world.Factory.WithDbAsync(db => db.AuditLog.AsNoTracking().Select(e => e.Detail + "|" + e.Subject).ToListAsync());
        Assert.DoesNotContain(all, text => text.Contains(code, StringComparison.Ordinal));
    }
}
