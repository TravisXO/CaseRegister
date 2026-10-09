using CaseRegister.Domain.Accounts;

namespace CaseRegister.Tests.Accounts;

public class MembershipNumberTests
{
    // The prefix is configuration. These tests use a neutral one so nothing depends on any real brand.
    private const string Prefix = "AGT";

    private static readonly string[] _allForms = ["AGT 884", "AGT884", "AGT-884", "884", "agt884"];

    [Theory]
    [InlineData("AGT 884", 884)]
    [InlineData("AGT884", 884)]
    [InlineData("AGT-884", 884)]
    [InlineData("884", 884)]
    [InlineData("agt 884", 884)]
    [InlineData("Agt884", 884)]
    [InlineData("aGt-884", 884)]
    [InlineData("agt884", 884)]
    [InlineData("  AGT 884  ", 884)]
    [InlineData(" 884 ", 884)]
    [InlineData("1", 1)]
    [InlineData("AGT 1", 1)]
    [InlineData("999999", 999_999)]
    [InlineData("AGT 999999", 999_999)]
    [InlineData("AGT-000884", 884)]
    [InlineData("000884", 884)]
    public void Accepted_forms_parse_to_the_integer(string input, int expected)
    {
        Assert.True(MembershipNumber.TryParse(input, Prefix, out var number));
        Assert.Equal(expected, number.Value);
        Assert.Equal(expected, MembershipNumber.Parse(input, Prefix).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("000000")]
    [InlineData("AGT 0")]
    [InlineData("1000000")]
    [InlineData("AGT 1000000")]
    [InlineData("9999999")]
    [InlineData("AGT")]
    [InlineData("AGT ")]
    [InlineData("AGT  884")]
    [InlineData("AGT - 884")]
    [InlineData("AGT--884")]
    [InlineData("AGT_884")]
    [InlineData("AGT.884")]
    [InlineData("AGT/884")]
    [InlineData("AG 884")]
    [InlineData("AGTT 884")]
    [InlineData("XAGT 884")]
    [InlineData("884 AGT")]
    [InlineData("AGT 884a")]
    [InlineData("a884")]
    [InlineData("-884")]
    [InlineData("+884")]
    [InlineData("8.84")]
    [InlineData("1,000")]
    [InlineData("88 4")]
    [InlineData("AGT 88 4")]
    [InlineData("AGT\t884")]
    [InlineData("AGT\n884")]
    [InlineData("AGT 884\n885")]
    [InlineData("٨٨٤")] // Arabic-Indic digits
    [InlineData("AGT ٨٨٤")]
    [InlineData("ＡＧＴ 884")] // full-width letters
    public void Rejected_forms_do_not_parse(string? input)
    {
        Assert.False(MembershipNumber.TryParse(input, Prefix, out var number));
        Assert.Null(number);
        Assert.Throws<FormatException>(() => MembershipNumber.Parse(input, Prefix));
    }

    [Theory]
    [InlineData(884, "AGT 884")]
    [InlineData(1, "AGT 1")]
    [InlineData(999_999, "AGT 999999")]
    public void It_is_displayed_as_the_prefix_a_space_and_the_number(int value, string expected)
    {
        Assert.Equal(expected, MembershipNumber.FromValue(value).Format(Prefix));
    }

    [Fact]
    public void ToString_is_just_the_number_because_the_prefix_is_not_the_domains_to_know()
    {
        Assert.Equal("884", MembershipNumber.FromValue(884).ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    [InlineData(int.MaxValue)]
    public void A_value_outside_one_to_999999_is_refused(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MembershipNumber.FromValue(value));
    }

    [Fact]
    public void Every_accepted_form_of_a_number_is_equal()
    {
        var forms = _allForms.Select(form => MembershipNumber.Parse(form, Prefix)).ToList();

        Assert.All(forms, form => Assert.Equal(forms[0], form));
        Assert.NotEqual(MembershipNumber.FromValue(885), forms[0]);
    }

    // --- The prefix is a setting ---------------------------------------------------------------

    [Fact]
    public void A_different_prefix_is_a_different_set_of_accepted_forms()
    {
        Assert.True(MembershipNumber.TryParse("REG-884", "REG", out var number));
        Assert.Equal(884, number.Value);

        // The old prefix means nothing once the setting changes; the bare number still works.
        Assert.False(MembershipNumber.TryParse("AGT 884", "REG", out _));
        Assert.True(MembershipNumber.TryParse("884", "REG", out _));
    }

    [Fact]
    public void The_prefix_is_matched_literally_not_as_a_pattern()
    {
        Assert.True(MembershipNumber.TryParse("A.B 884", "A.B", out _));
        Assert.False(MembershipNumber.TryParse("AxB 884", "A.B", out _));
        Assert.True(MembershipNumber.TryParse("(X) 884", "(X)", out _));
    }

    [Fact]
    public void The_display_form_uses_whatever_prefix_it_is_given()
    {
        var number = MembershipNumber.FromValue(7);

        Assert.Equal("REG 7", number.Format("REG"));
        Assert.Equal("Agent 7", number.Format("Agent"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_prefix_is_a_programming_error_not_a_silent_default(string? prefix)
    {
        Assert.ThrowsAny<ArgumentException>(() => MembershipNumber.TryParse("884", prefix!, out _));
        Assert.ThrowsAny<ArgumentException>(() => MembershipNumber.FromValue(884).Format(prefix!));
    }

    [Fact]
    public void The_domain_project_does_not_hard_code_any_brand_name()
    {
        // Membership numbers are shown with a configurable prefix; the Domain must not know which.
        var domainFolder = Path.Combine(Architecture.SolutionGraph.SolutionRoot, "CaseRegister.Domain");
        var offenders = Directory.EnumerateFiles(domainFolder, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("ziea", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }
}
