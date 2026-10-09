using CaseRegister.Domain.Accounts;

namespace CaseRegister.Tests.Accounts;

public class EmailNormaliserTests
{
    [Theory]
    [InlineData("Jane@Example.COM", "jane@example.com")]
    [InlineData("  jane@example.com  ", "jane@example.com")]
    [InlineData("\tJANE@EXAMPLE.COM\n", "jane@example.com")]
    [InlineData("ＪＡＮＥ@example.com", "jane@example.com")] // full-width letters fold to ASCII
    [InlineData("jane+tag@example.com", "jane+tag@example.com")]
    public void Emails_are_trimmed_unicode_normalised_and_lower_cased(string input, string expected)
    {
        Assert.Equal(expected, EmailNormaliser.Normalise(input));
    }

    [Fact]
    public void Normalising_twice_changes_nothing()
    {
        var once = EmailNormaliser.Normalise("  Jane.Doe@Example.COM ");

        Assert.Equal(once, EmailNormaliser.Normalise(once));
        Assert.True(EmailNormaliser.IsNormalised(once));
    }

    [Theory]
    [InlineData("Jane@example.com")]
    [InlineData(" jane@example.com")]
    [InlineData("jane@example.com ")]
    public void An_address_that_is_not_in_normal_form_is_reported(string input)
    {
        Assert.False(EmailNormaliser.IsNormalised(input));
    }

    [Fact]
    public void Null_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => EmailNormaliser.Normalise(null!));
        Assert.False(EmailNormaliser.IsNormalised(null!));
    }
}
