using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Certificates;
using CaseRegister.Domain.Invites;

namespace CaseRegister.Tests.Accounts;

public class AgencyNamesTests
{
    [Theory]
    [InlineData("Copperbelt Homes Ltd", "copperbelt homes")]
    [InlineData("COPPERBELT HOMES LIMITED", "copperbelt homes")]
    [InlineData("Copperbelt Homes, Ltd.", "copperbelt homes")]
    [InlineData("Copperbelt Homes L.T.D.", "copperbelt homes")]
    [InlineData("  Copperbelt   Homes  ", "copperbelt homes")]
    [InlineData("Copperbelt-Homes", "copperbelt homes")]
    [InlineData("Copperbelt Homes Co. Ltd", "copperbelt homes")] // endings come off one after another
    [InlineData("Smith & Sons Limited", "smith and sons")]
    [InlineData("Smith and Sons Plc", "smith and sons")]
    [InlineData("Mwansa's Property Inc", "mwansas property")]
    [InlineData("Limited", "limited")] // never the whole name
    [InlineData("Ltd Co", "ltd")]
    [InlineData("Agency 7 Ltd", "agency 7")]
    public void Names_are_compared_without_case_punctuation_or_company_endings(string name, string expected)
    {
        Assert.Equal(expected, AgencyNames.Normalise(name));
    }

    [Theory]
    [InlineData("Copperbelt Homes Ltd", "COPPERBELT HOMES LIMITED", true)]
    [InlineData("Copperbelt Homes", "Copperbelt Homes, Ltd.", true)]
    [InlineData("Copperbelt Homes", "Copperbelt Homes Zambia", false)] // a different name is a different agency
    [InlineData("Copperbelt Homes", "Copper Homes", false)]
    [InlineData("Smith & Sons", "Smith and Sons Ltd", true)]
    [InlineData("Lusaka Realty", "Lusaka Reality", false)]
    [InlineData("...", "!!!", false)] // nothing left to compare
    public void Two_names_are_the_same_agency_only_when_they_normalise_equal(string first, string second, bool same)
    {
        Assert.Equal(same, AgencyNames.AreSame(first, second));
        Assert.Equal(same, AgencyNames.AreSame(second, first));
    }
}

public class MemberApplicationTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid _user = Guid.NewGuid();
    private static readonly Guid _admin = Guid.NewGuid();

    private static MemberApplication Application(ApplicationConcerns flags = ApplicationConcerns.None) =>
        MemberApplication.Submit(_user, MembershipNumber.FromValue(884), MembershipClass.Full, "  Copperbelt Homes  ", CertificateNumber.Parse("9611/2026"), flags, _now);

    [Fact]
    public void A_new_application_waits_with_the_claims_as_made()
    {
        var application = Application();

        Assert.Equal(ApplicationStatus.Pending, application.Status);
        Assert.Equal(_user, application.UserId);
        Assert.Equal(884, application.MembershipNumber.Value);
        Assert.Equal("Copperbelt Homes", application.AgencyName); // trimmed
        Assert.Equal(2026, application.CertificateNumber.Year);
        Assert.Equal(_now, application.SubmittedAt);
        Assert.Equal(0, application.ReapplicationCount);
    }

    [Fact]
    public void An_agency_name_is_needed_and_has_a_limit()
    {
        var number = MembershipNumber.FromValue(1);
        var certificate = CertificateNumber.Parse("1/2026");

        Assert.Throws<ArgumentException>(() => MemberApplication.Submit(_user, number, MembershipClass.Full, "  ", certificate, ApplicationConcerns.None, _now));
        Assert.Throws<ArgumentException>(() => MemberApplication.Submit(_user, number, MembershipClass.Full, new string('a', 201), certificate, ApplicationConcerns.None, _now));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemberApplication.Submit(_user, number, (MembershipClass)99, "Agency", certificate, ApplicationConcerns.None, _now));
        MemberApplication.Submit(_user, number, MembershipClass.Full, new string('a', 200), certificate, ApplicationConcerns.None, _now);
    }

    [Fact]
    public void Approving_records_who_when_and_which_agency_and_only_once()
    {
        var application = Application();
        var agency = Guid.NewGuid();

        application.Approve(_admin, agency, _now.AddHours(1));

        Assert.Equal(ApplicationStatus.Approved, application.Status);
        Assert.Equal(_admin, application.DecidedByUserId);
        Assert.Equal(agency, application.ApprovedAgencyId);
        Assert.Throws<InvalidOperationException>(() => application.Approve(_admin, agency, _now));
        Assert.Throws<InvalidOperationException>(() => application.Reject(_admin, "Not eligible.", _now));
    }

    [Fact]
    public void Rejecting_records_the_reason_and_only_once()
    {
        var application = Application();

        application.Reject(_admin, "  The certificate number is not on our register.  ", _now.AddHours(1));

        Assert.Equal(ApplicationStatus.Rejected, application.Status);
        Assert.Equal("The certificate number is not on our register.", application.DecisionReason);
        Assert.Throws<InvalidOperationException>(() => application.Reject(_admin, "Again.", _now));
        Assert.Throws<ArgumentException>(() => Application().Reject(_admin, " ", _now));
    }

    [Fact]
    public void Only_a_rejected_application_can_be_submitted_again_and_it_is_flagged_as_a_reapplication()
    {
        var waiting = Application();
        var approved = Application();
        approved.Approve(_admin, Guid.NewGuid(), _now);
        var rejected = Application(ApplicationConcerns.DuplicateMembershipNumber);
        rejected.Reject(_admin, "The certificate number is not on our register.", _now);

        Assert.Throws<InvalidOperationException>(() => waiting.Resubmit(MembershipNumber.FromValue(5), MembershipClass.Associate, "New", CertificateNumber.Parse("2/2026"), ApplicationConcerns.None, _now));
        Assert.Throws<InvalidOperationException>(() => approved.Resubmit(MembershipNumber.FromValue(5), MembershipClass.Associate, "New", CertificateNumber.Parse("2/2026"), ApplicationConcerns.None, _now));

        rejected.Resubmit(MembershipNumber.FromValue(5), MembershipClass.Associate, "New Agency", CertificateNumber.Parse("2/2026"), ApplicationConcerns.None, _now.AddDays(3));

        Assert.Equal(ApplicationStatus.Pending, rejected.Status);
        Assert.Equal(ApplicationConcerns.Reapplication, rejected.Flags); // the new flags, plus the reapplication
        Assert.Equal(1, rejected.ReapplicationCount);
        Assert.Equal(5, rejected.MembershipNumber.Value);
        Assert.Equal(MembershipClass.Associate, rejected.MembershipClass);
        Assert.Equal("New Agency", rejected.AgencyName);
        Assert.Equal(_now.AddDays(3), rejected.SubmittedAt);
        Assert.Null(rejected.DecisionReason);
        Assert.Null(rejected.DecidedByUserId);
    }

    [Fact]
    public void Flags_have_names_in_a_fixed_order_and_read_back()
    {
        var both = ApplicationConcerns.DuplicateMembershipNumber | ApplicationConcerns.Reapplication;

        Assert.Equal(["Reapplication", "DuplicateMembershipNumber"], ApplicationFlagNames.Of(both));
        Assert.Empty(ApplicationFlagNames.Of(ApplicationConcerns.None));
        Assert.True(ApplicationFlagNames.TryParse(["DuplicateMembershipNumber", "Reapplication"], out var parsed));
        Assert.Equal(both, parsed);
        Assert.True(ApplicationFlagNames.TryParse([], out var none));
        Assert.Equal(ApplicationConcerns.None, none);
    }

    [Theory]
    [InlineData("None")]
    [InlineData("reapplication")] // exact names only
    [InlineData("Everything")]
    [InlineData("3")]
    [InlineData("")]
    public void Anything_that_is_not_a_flag_name_is_refused(string name)
    {
        Assert.False(ApplicationFlagNames.TryParse([name], out _));
    }

    [Theory]
    [InlineData(2026, 1, 1, new[] { 2026 })]
    [InlineData(2026, 6, 15, new[] { 2026 })]
    [InlineData(2026, 11, 30, new[] { 2026 })]
    [InlineData(2026, 12, 1, new[] { 2026, 2027 })]
    [InlineData(2026, 12, 31, new[] { 2026, 2027 })]
    [InlineData(2027, 1, 1, new[] { 2027 })]
    public void The_certificate_year_is_the_current_one_and_in_December_next_years_too(int year, int month, int day, int[] expected)
    {
        Assert.Equal(expected, ApplicationRules.AcceptedCertificateYears(new DateOnly(year, month, day)));
    }
}

public class StaffSetupTokenTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_token_is_256_random_bits_in_a_form_that_is_safe_in_a_url()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => StaffSetupToken.NewToken()).ToList();

        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, token =>
        {
            Assert.Equal(43, token.Length); // 32 bytes in base64 without padding
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
        });
        Assert.Equal(32, StaffSetupToken.TokenBytes);
    }

    [Fact]
    public void Only_the_sha256_hash_is_what_is_stored_and_it_is_not_the_token()
    {
        const string token = "abc";

        // SHA-256("abc"), the standard test value.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", StaffSetupToken.HashOf(token));
        var issued = StaffSetupToken.Issue(Guid.NewGuid(), StaffSetupToken.HashOf(token), Guid.NewGuid(), _now);
        Assert.DoesNotContain(token, issued.TokenHash, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_lasts_exactly_twenty_four_hours()
    {
        var token = StaffSetupToken.Issue(Guid.NewGuid(), StaffSetupToken.HashOf("x"), Guid.NewGuid(), _now);

        Assert.Equal(TimeSpan.FromHours(24), StaffSetupToken.Lifetime);
        Assert.Equal(_now.AddHours(24), token.ExpiresAt);
        Assert.True(token.IsUsable(_now));
        Assert.True(token.IsUsable(_now.AddHours(24).AddTicks(-1)));
        Assert.False(token.IsUsable(_now.AddHours(24)));
        Assert.Equal("expired", token.Problem(_now.AddHours(25)));
        Assert.Null(token.Problem(_now));
    }

    [Fact]
    public void A_token_works_once()
    {
        var token = StaffSetupToken.Issue(Guid.NewGuid(), StaffSetupToken.HashOf("x"), Guid.NewGuid(), _now);

        token.Spend(_now.AddHours(1));

        Assert.False(token.IsUsable(_now.AddHours(2)));
        Assert.Equal("already used", token.Problem(_now.AddHours(2)));
        Assert.Throws<InvalidOperationException>(() => token.Spend(_now.AddHours(2)));
    }

    [Fact]
    public void A_replaced_token_cannot_be_used_and_a_spent_one_is_not_replaced()
    {
        var replaced = StaffSetupToken.Issue(Guid.NewGuid(), StaffSetupToken.HashOf("x"), Guid.NewGuid(), _now);
        var spent = StaffSetupToken.Issue(Guid.NewGuid(), StaffSetupToken.HashOf("y"), Guid.NewGuid(), _now);
        spent.Spend(_now);

        replaced.Revoke(_now.AddHours(1));
        spent.Revoke(_now.AddHours(1));

        Assert.False(replaced.IsUsable(_now.AddHours(2)));
        Assert.Equal("replaced by a newer invite", replaced.Problem(_now.AddHours(2)));
        Assert.Null(spent.RevokedAt);
        Assert.Throws<InvalidOperationException>(() => replaced.Spend(_now.AddHours(2)));
    }
}

public class PendingActionEmailTargetTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_invite_targets_an_email_address_and_nothing_else()
    {
        var invite = PendingAction.Propose(PendingActionType.InviteStaff, null, null, "{}", "Reason that is long enough.", Guid.NewGuid(), _now, "new.person@example.test");

        Assert.Equal("new.person@example.test", invite.TargetEmail);
        Assert.Null(invite.TargetUserId);
        Assert.True(invite.IsIntact);
        Assert.Throws<ArgumentException>(() => PendingAction.Propose(PendingActionType.InviteStaff, Guid.NewGuid(), null, "{}", "Reason that is long enough.", Guid.NewGuid(), _now, "x@example.test"));
        Assert.Throws<ArgumentException>(() => PendingAction.Propose(PendingActionType.InviteStaff, null, null, "{}", "Reason that is long enough.", Guid.NewGuid(), _now));
    }

    [Fact]
    public void The_hash_covers_the_email_address()
    {
        string Hash(string email) => PendingAction.HashOf(PendingActionType.InviteStaff, null, null, "{}", email);

        Assert.NotEqual(Hash("a@example.test"), Hash("b@example.test"));
        Assert.Equal(Hash("a@example.test"), Hash("a@example.test"));

        // And proposals that have no email hash exactly as before.
        var user = Guid.NewGuid();
        Assert.Equal(PendingAction.HashOf(PendingActionType.EndSessions, user, null, "{}"), PendingAction.HashOf(PendingActionType.EndSessions, user, null, "{}", null));
    }
}
