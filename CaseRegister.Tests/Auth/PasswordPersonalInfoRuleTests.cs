using CaseRegister.Application.Auth;
using CaseRegister.Domain.Accounts;

namespace CaseRegister.Tests.Auth;

public class PasswordPersonalInfoRuleTests
{
    private const string Prefix = "AGT";
    private const string Email = "jane.mwansa@example.com";
    private const string Name = "Jane Mwansa Banda";
    private static readonly MembershipNumber _number = MembershipNumber.FromValue(884);
    private static readonly MembershipNumber _longNumber = MembershipNumber.FromValue(123456);

    private static bool Rejected(string password, MembershipNumber? number = null) =>
        PasswordPersonalInfoRule.ContainsPersonalInfo(password, Email, Name, number ?? _number, Prefix);

    [Theory]
    [InlineData("jane.mwansa@example.com")]
    [InlineData("xx JANE.MWANSA@EXAMPLE.COM xx")] // any letter case
    [InlineData("my-jane.mwansa-secret")] // the part before the @
    [InlineData("ilovemwansa2024")] // a part of the local part
    public void The_email_or_a_meaningful_part_of_it_is_rejected(string password)
    {
        Assert.True(Rejected(password));
    }

    [Theory]
    [InlineData("jane's secret pass")]
    [InlineData("MWANSA is my name")]
    [InlineData("bandaFamily2024!!")]
    [InlineData("janemwansabanda1")] // the whole name run together
    [InlineData("xxxjanexxxxxxxxx")]
    public void Any_part_of_the_name_is_rejected(string password)
    {
        Assert.True(Rejected(password));
    }

    [Theory]
    [InlineData("AGT 884 forever")]
    [InlineData("agt884isme!!")]
    [InlineData("my AGT-884 number")]
    [InlineData("AgT 884 AgT")]
    public void The_membership_number_in_any_accepted_form_is_rejected(string password)
    {
        Assert.True(Rejected(password));
    }

    [Fact]
    public void A_short_membership_number_on_its_own_is_not_a_match()
    {
        // "884" appears in countless unrelated passwords. Only the prefixed forms count for short numbers.
        Assert.False(Rejected("correct horse 884 staple"));
    }

    [Fact]
    public void A_long_membership_number_on_its_own_is_a_match()
    {
        Assert.True(Rejected("correct 123456 staple", _longNumber));
        Assert.False(Rejected("correct 654321 staple", _longNumber));
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("hunter2hunter2hunter2")]
    [InlineData("jan is not enough")] // "jan" is shorter than any name part
    [InlineData("exam ple.com is not it")]
    public void Unrelated_passwords_are_accepted(string password)
    {
        Assert.False(Rejected(password));
    }

    [Fact]
    public void Missing_details_are_skipped_rather_than_matching_everything()
    {
        Assert.False(PasswordPersonalInfoRule.ContainsPersonalInfo("correct horse battery", string.Empty, string.Empty, null, Prefix));
        Assert.False(PasswordPersonalInfoRule.ContainsPersonalInfo("correct horse battery", "  ", "  ", null, Prefix));
    }

    [Fact]
    public void A_short_local_part_is_not_treated_as_a_secret()
    {
        // "jo" would match far too many passwords, but the whole address still counts.
        Assert.False(PasswordPersonalInfoRule.ContainsPersonalInfo("a major joke here", "jo@example.com", "X", null, Prefix));
        Assert.True(PasswordPersonalInfoRule.ContainsPersonalInfo("jo@example.com!!", "jo@example.com", "X", null, Prefix));
    }
}
