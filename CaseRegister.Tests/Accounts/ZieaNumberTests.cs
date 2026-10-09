using CaseRegister.Domain.Accounts;

namespace CaseRegister.Tests.Accounts;

public class ZieaNumberTests
{
    private static readonly string[] _allForms = ["ZIEA 884", "ZIEA884", "ZIEA-884", "884", "ziea884"];

    [Theory]
    [InlineData("ZIEA 884", 884)]
    [InlineData("ZIEA884", 884)]
    [InlineData("ZIEA-884", 884)]
    [InlineData("884", 884)]
    [InlineData("ziea 884", 884)]
    [InlineData("Ziea884", 884)]
    [InlineData("zIeA-884", 884)]
    [InlineData("ziea884", 884)]
    [InlineData("  ZIEA 884  ", 884)]
    [InlineData(" 884 ", 884)]
    [InlineData("1", 1)]
    [InlineData("ZIEA 1", 1)]
    [InlineData("999999", 999_999)]
    [InlineData("ZIEA 999999", 999_999)]
    [InlineData("ZIEA-000884", 884)]
    [InlineData("000884", 884)]
    public void Accepted_forms_parse_to_the_integer(string input, int expected)
    {
        Assert.True(ZieaNumber.TryParse(input, out var number));
        Assert.Equal(expected, number.Value);
        Assert.Equal(expected, ZieaNumber.Parse(input).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("000000")]
    [InlineData("ZIEA 0")]
    [InlineData("1000000")]
    [InlineData("ZIEA 1000000")]
    [InlineData("9999999")]
    [InlineData("ZIEA")]
    [InlineData("ZIEA ")]
    [InlineData("ZIEA  884")]
    [InlineData("ZIEA - 884")]
    [InlineData("ZIEA--884")]
    [InlineData("ZIEA_884")]
    [InlineData("ZIEA.884")]
    [InlineData("ZIEA/884")]
    [InlineData("ZIE 884")]
    [InlineData("ZIEAA 884")]
    [InlineData("XZIEA 884")]
    [InlineData("884 ZIEA")]
    [InlineData("ZIEA 884a")]
    [InlineData("a884")]
    [InlineData("-884")]
    [InlineData("+884")]
    [InlineData("8.84")]
    [InlineData("1,000")]
    [InlineData("88 4")]
    [InlineData("ZIEA 88 4")]
    [InlineData("ZIEA\t884")]
    [InlineData("ZIEA\n884")]
    [InlineData("ZIEA 884\n885")]
    [InlineData("٨٨٤")] // Arabic-Indic digits
    [InlineData("ZIEA ٨٨٤")]
    [InlineData("ＺＩＥＡ 884")] // full-width letters
    public void Rejected_forms_do_not_parse(string? input)
    {
        Assert.False(ZieaNumber.TryParse(input, out var number));
        Assert.Null(number);
        Assert.Throws<FormatException>(() => ZieaNumber.Parse(input));
    }

    [Theory]
    [InlineData(884, "ZIEA 884")]
    [InlineData(1, "ZIEA 1")]
    [InlineData(999_999, "ZIEA 999999")]
    public void It_is_always_displayed_as_ZIEA_space_number(int value, string expected)
    {
        Assert.Equal(expected, ZieaNumber.FromValue(value).ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    [InlineData(int.MaxValue)]
    public void A_value_outside_one_to_999999_is_refused(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ZieaNumber.FromValue(value));
    }

    [Fact]
    public void Every_accepted_form_of_a_number_is_equal()
    {
        var forms = _allForms.Select(ZieaNumber.Parse).ToList();

        Assert.All(forms, form => Assert.Equal(forms[0], form));
        Assert.NotEqual(ZieaNumber.FromValue(885), forms[0]);
    }
}
