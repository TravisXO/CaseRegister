using CaseRegister.Domain.Approvals;

namespace CaseRegister.Tests.Accounts;

public class ApprovalRulesTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid _maker = Guid.NewGuid();
    private static readonly Guid _checker = Guid.NewGuid();
    private static readonly Guid _target = Guid.NewGuid();

    private static PendingAction Proposal(
        PendingActionType type = PendingActionType.ResetTwoFactor,
        Guid? targetUser = null,
        Guid? targetAgency = null,
        string payload = "{}")
    {
        var user = targetUser ?? (targetAgency is null ? _target : null);

        return PendingAction.Propose(type, user, targetAgency, payload, "Reason that is long enough.", _maker, _start);
    }

    // --- Which actions need two admins -----------------------------------------------------------

    [Theory]
    [InlineData(PendingActionType.AssignRole, false, true)]
    [InlineData(PendingActionType.AssignRole, true, true)]
    [InlineData(PendingActionType.RemoveRole, false, true)]
    [InlineData(PendingActionType.RemoveRole, true, true)]
    [InlineData(PendingActionType.VerifyPractisingCertificate, false, true)]
    [InlineData(PendingActionType.VerifyPractisingCertificate, true, true)]
    [InlineData(PendingActionType.ReactivateUser, false, true)]
    [InlineData(PendingActionType.ReactivateUser, true, true)]
    [InlineData(PendingActionType.ReactivateAgency, false, true)]
    [InlineData(PendingActionType.ResetTwoFactor, false, true)]
    [InlineData(PendingActionType.ResetTwoFactor, true, true)]
    [InlineData(PendingActionType.ApproveApplication, false, true)]
    [InlineData(PendingActionType.RejectApplication, false, true)]
    [InlineData(PendingActionType.InviteStaff, false, true)]
    [InlineData(PendingActionType.SuspendUser, false, false)]
    [InlineData(PendingActionType.SuspendUser, true, true)]
    [InlineData(PendingActionType.EndSessions, false, false)]
    [InlineData(PendingActionType.EndSessions, true, true)]
    [InlineData(PendingActionType.SuspendAgency, false, false)]
    public void Granting_always_needs_two_admins_and_taking_away_only_when_the_target_is_an_admin(PendingActionType type, bool targetIsAdmin, bool needsTwo)
    {
        Assert.Equal(needsTwo, ApprovalRules.RequiresTwoAdmins(type, targetIsAdmin));
    }

    [Fact]
    public void Every_action_type_is_covered_by_the_table_above()
    {
        // 12 types; the theory above has a row for each in the not-an-admin case.
        var covered = new[]
        {
            PendingActionType.AssignRole, PendingActionType.RemoveRole, PendingActionType.VerifyPractisingCertificate,
            PendingActionType.ReactivateUser, PendingActionType.ReactivateAgency, PendingActionType.ResetTwoFactor,
            PendingActionType.ApproveApplication, PendingActionType.RejectApplication, PendingActionType.InviteStaff,
            PendingActionType.SuspendUser, PendingActionType.EndSessions, PendingActionType.SuspendAgency,
        };

        Assert.Equal(covered.Order(), Enum.GetValues<PendingActionType>().Order());
    }

    [Fact]
    public void Only_suspending_a_user_and_removing_a_role_can_lower_the_number_of_admins()
    {
        Assert.Equal(
            [PendingActionType.RemoveRole, PendingActionType.SuspendUser],
            Enum.GetValues<PendingActionType>().Where(ApprovalRules.CanReduceAdmins).Order());
    }

    // --- The two-admin minimum ------------------------------------------------------------------------

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, 2, true)]
    [InlineData(4, 2, false)]
    [InlineData(4, 3, true)]
    [InlineData(1, 1, true)]
    [InlineData(2, 0, false)]
    [InlineData(1, 0, false)] // already below the minimum, but this loses no one
    [InlineData(0, 0, false)]
    public void A_change_that_would_leave_fewer_than_two_active_admins_is_refused(int active, int lost, bool refused)
    {
        Assert.Equal(refused, ApprovalRules.WouldLeaveTooFewAdmins(active, lost));
    }

    [Fact]
    public void The_minimum_is_two_and_a_proposal_lasts_seventy_two_hours()
    {
        Assert.Equal(2, ApprovalRules.MinimumActiveAdmins);
        Assert.Equal(TimeSpan.FromHours(72), ApprovalRules.Lifetime);
    }

    // --- The reason ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("         ", false)]
    [InlineData("123456789", false)] // 9
    [InlineData("1234567890", true)] // 10
    [InlineData("   1234567890   ", true)] // trimmed: 10
    [InlineData("   123456789   ", false)] // trimmed: 9
    public void A_reason_is_ten_characters_at_least(string? reason, bool accepted)
    {
        Assert.Equal(accepted, ApprovalRules.CheckReason(reason) is null);
    }

    [Fact]
    public void A_reason_is_five_hundred_characters_at_most()
    {
        Assert.Null(ApprovalRules.CheckReason(new string('a', 500)));
        Assert.NotNull(ApprovalRules.CheckReason(new string('a', 501)));
        Assert.Null(ApprovalRules.CheckReason("  " + new string('a', 500) + "  ")); // the spaces are not counted
    }

    // --- Nobody acts on themselves ------------------------------------------------------------------------

    [Fact]
    public void An_action_targeting_yourself_is_refused()
    {
        var agency = Guid.NewGuid();

        Assert.True(ApprovalRules.IsSelfTarget(_maker, null, _maker, null));
        Assert.False(ApprovalRules.IsSelfTarget(_maker, null, _target, null));
        Assert.True(ApprovalRules.IsSelfTarget(_maker, agency, null, agency)); // your own agency
        Assert.False(ApprovalRules.IsSelfTarget(_maker, agency, null, Guid.NewGuid()));
        Assert.False(ApprovalRules.IsSelfTarget(_maker, null, null, agency)); // staff have no agency to be targeted through
        Assert.Equal(ApprovalRules.SelfTarget, Assert.Single(ApprovalRules.CheckProposal(_maker, null, _maker, null)).Code);
        Assert.Empty(ApprovalRules.CheckProposal(_maker, null, _target, null));
    }

    // --- A proposal ----------------------------------------------------------------------------------

    [Fact]
    public void A_proposal_expires_exactly_seventy_two_hours_after_it_was_made()
    {
        var proposal = Proposal();

        Assert.Equal(_start, proposal.ProposedAt);
        Assert.Equal(_start.AddHours(72), proposal.ExpiresAt);
        Assert.Equal(PendingActionStatus.Pending, proposal.EffectiveStatus(_start));
        Assert.Equal(PendingActionStatus.Pending, proposal.EffectiveStatus(_start.AddHours(72).AddTicks(-1)));
        Assert.Equal(PendingActionStatus.Expired, proposal.EffectiveStatus(_start.AddHours(72)));
        Assert.Equal(PendingActionStatus.Expired, proposal.EffectiveStatus(_start.AddDays(30)));
        Assert.Equal(PendingActionStatus.Pending, proposal.Status); // nothing is written by looking
    }

    [Fact]
    public void A_proposal_targets_exactly_one_user_or_one_agency()
    {
        Assert.Throws<ArgumentException>(() => PendingAction.Propose(PendingActionType.ResetTwoFactor, null, null, "{}", "Reason that is long enough.", _maker, _start));
        Assert.Throws<ArgumentException>(() => PendingAction.Propose(PendingActionType.ResetTwoFactor, _target, Guid.NewGuid(), "{}", "Reason that is long enough.", _maker, _start));
        Assert.Equal(_target, Proposal().TargetUserId);
    }

    [Fact]
    public void A_proposal_needs_a_proper_reason()
    {
        Assert.Throws<ArgumentException>(() => PendingAction.Propose(PendingActionType.ResetTwoFactor, _target, null, "{}", "too short", _maker, _start));
    }

    [Fact]
    public void The_hash_covers_the_type_the_target_and_the_payload()
    {
        var other = Guid.NewGuid();
        var hash = Proposal().PayloadHash;

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, Proposal().PayloadHash); // the same proposal, the same hash
        Assert.NotEqual(hash, Proposal(PendingActionType.EndSessions).PayloadHash);
        Assert.NotEqual(hash, Proposal(targetUser: other).PayloadHash);
        Assert.NotEqual(hash, Proposal(payload: "{\"role\":\"Admin\"}").PayloadHash);
        Assert.NotEqual(Proposal(payload: "{\"role\":\"Admin\"}").PayloadHash, Proposal(payload: "{\"role\":\"CaseReviewer\"}").PayloadHash);
        Assert.NotEqual(Proposal(targetUser: other).PayloadHash, Proposal(PendingActionType.ReactivateAgency, targetUser: null, targetAgency: other).PayloadHash);
    }

    [Fact]
    public void Only_the_exact_hash_matches()
    {
        var proposal = Proposal();

        Assert.True(proposal.HashMatches(proposal.PayloadHash));
        Assert.True(proposal.HashMatches(proposal.PayloadHash.ToUpperInvariant())); // how it is copied does not matter
        Assert.True(proposal.HashMatches("  " + proposal.PayloadHash + " "));
        Assert.False(proposal.HashMatches(null));
        Assert.False(proposal.HashMatches(string.Empty));
        Assert.False(proposal.HashMatches(proposal.PayloadHash[..63]));
        Assert.False(proposal.HashMatches(new string('0', 64)));
        Assert.False(proposal.HashMatches(Proposal(PendingActionType.EndSessions).PayloadHash));
    }

    [Fact]
    public void A_proposal_is_intact_until_the_hash_stops_matching_what_it_holds()
    {
        Assert.True(Proposal().IsIntact);
    }

    // --- Deciding ----------------------------------------------------------------------------------------

    [Fact]
    public void A_different_admin_confirms_and_it_records_who_and_when()
    {
        var proposal = Proposal();

        proposal.Confirm(_checker, _start.AddHours(1));

        Assert.Equal(PendingActionStatus.Confirmed, proposal.Status);
        Assert.Equal(_checker, proposal.DecidedByUserId);
        Assert.Equal(_start.AddHours(1), proposal.DecidedAt);
    }

    [Fact]
    public void A_decided_proposal_cannot_be_decided_again()
    {
        var confirmed = Proposal();
        confirmed.Confirm(_checker, _start);
        var rejected = Proposal();
        rejected.Reject(_checker, _start, "Not needed after all, thanks.");
        var withdrawn = Proposal();
        withdrawn.Withdraw(_maker, _start, "Proposed by mistake, sorry.");

        foreach (var decided in new[] { confirmed, rejected, withdrawn })
        {
            Assert.Throws<InvalidOperationException>(() => decided.Confirm(_checker, _start));
            Assert.Throws<InvalidOperationException>(() => decided.Reject(_checker, _start, "Reason that is long enough."));
            Assert.Throws<InvalidOperationException>(() => decided.Withdraw(_maker, _start, "Reason that is long enough."));
        }
    }

    [Fact]
    public void An_expired_proposal_cannot_be_decided()
    {
        var proposal = Proposal();

        Assert.Throws<InvalidOperationException>(() => proposal.Confirm(_checker, _start.AddHours(72)));
        proposal.Confirm(_checker, _start.AddHours(72).AddTicks(-1));
        Assert.Equal(PendingActionStatus.Confirmed, proposal.Status);
    }

    [Fact]
    public void Only_the_proposer_can_withdraw_and_a_reason_is_needed_to_reject_or_withdraw()
    {
        var proposal = Proposal();

        Assert.Throws<InvalidOperationException>(() => proposal.Withdraw(_checker, _start, "Reason that is long enough."));
        Assert.Throws<ArgumentException>(() => proposal.Withdraw(_maker, _start, "short"));
        Assert.Throws<ArgumentException>(() => proposal.Reject(_checker, _start, "short"));
        Assert.Equal(PendingActionStatus.Pending, proposal.Status);

        proposal.Withdraw(_maker, _start, "  Proposed by mistake, sorry.  ");

        Assert.Equal(PendingActionStatus.Withdrawn, proposal.Status);
        Assert.Equal("Proposed by mistake, sorry.", proposal.DecisionReason);
    }

    [Fact]
    public void Expiring_is_only_possible_once_the_time_has_run_out_and_is_dated_at_the_moment_it_lapsed()
    {
        var proposal = Proposal();

        Assert.Throws<InvalidOperationException>(() => proposal.Expire(_start.AddHours(71)));

        proposal.Expire(_start.AddDays(10));

        Assert.Equal(PendingActionStatus.Expired, proposal.Status);
        Assert.Equal(_start.AddHours(72), proposal.DecidedAt);
        Assert.Null(proposal.DecidedByUserId);
    }

    [Fact]
    public void Recording_a_refusal_leaves_the_proposal_pending()
    {
        var proposal = Proposal();

        proposal.RecordRefusal(new ApprovalViolation("MinimumAdmins", "Too few."), _start.AddHours(2));

        Assert.Equal(PendingActionStatus.Pending, proposal.Status);
        Assert.Equal("MinimumAdmins", proposal.LastRefusalCode);
        Assert.Equal("Too few.", proposal.LastRefusalMessage);
        Assert.Equal(_start.AddHours(2), proposal.LastRefusedAt);
    }

    // --- The people-and-time rules --------------------------------------------------------------------------

    private static string[] Codes(IEnumerable<ApprovalViolation> violations) => [.. violations.Select(violation => violation.Code).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_different_admin_may_decide_a_pending_proposal()
    {
        Assert.Empty(ApprovalRules.CheckDecision(Proposal(), _checker, null, _start.AddHours(1)));
    }

    [Fact]
    public void The_proposer_may_not_decide_their_own_proposal()
    {
        Assert.Equal([ApprovalRules.SameAsProposer], Codes(ApprovalRules.CheckDecision(Proposal(), _maker, null, _start)));
    }

    [Fact]
    public void Nobody_decides_an_action_that_targets_them()
    {
        Assert.Equal([ApprovalRules.SelfTarget], Codes(ApprovalRules.CheckDecision(Proposal(), _target, null, _start)));
    }

    [Fact]
    public void An_expired_or_decided_proposal_is_not_open()
    {
        var done = Proposal();
        done.Confirm(_checker, _start);

        Assert.Equal([ApprovalRules.Expired], Codes(ApprovalRules.CheckOpen(Proposal(), _start.AddHours(72))));
        Assert.Equal([ApprovalRules.NotPending], Codes(ApprovalRules.CheckOpen(done, _start)));
        Assert.Empty(ApprovalRules.CheckOpen(Proposal(), _start));
    }

    [Fact]
    public void Every_rule_that_is_broken_is_reported()
    {
        // The proposer is also the target, and the time has run out.
        var proposal = PendingAction.Propose(PendingActionType.ResetTwoFactor, _maker, null, "{}", "Reason that is long enough.", _maker, _start);

        Assert.Equal(
            [ApprovalRules.Expired, ApprovalRules.SameAsProposer, ApprovalRules.SelfTarget],
            Codes(ApprovalRules.CheckDecision(proposal, _maker, null, _start.AddDays(5))));
    }
}
