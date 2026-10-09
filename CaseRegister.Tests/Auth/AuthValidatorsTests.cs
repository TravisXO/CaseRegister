using CaseRegister.Application.Auditing;
using CaseRegister.Application.Auth;

namespace CaseRegister.Tests.Auth;

public class AuthValidatorsTests
{
    private readonly SignInCommandValidator _signIn = new();
    private readonly ChangePasswordCommandValidator _change = new();

    [Fact]
    public void A_normal_sign_in_request_is_valid()
    {
        Assert.True(_signIn.Validate(new SignInCommand("jane@example.com", "password", "token")).IsValid);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("   ", "password")]
    [InlineData("jane@example.com", "")]
    public void Sign_in_needs_an_email_and_a_password(string email, string password)
    {
        Assert.False(_signIn.Validate(new SignInCommand(email, password, null)).IsValid);
    }

    [Fact]
    public void Sign_in_password_is_capped_at_128_characters_so_huge_input_never_reaches_the_hash()
    {
        Assert.True(_signIn.Validate(new SignInCommand("jane@example.com", new string('a', PasswordPolicy.MaximumLength), null)).IsValid);
        Assert.False(_signIn.Validate(new SignInCommand("jane@example.com", new string('a', PasswordPolicy.MaximumLength + 1), null)).IsValid);
    }

    [Fact]
    public void Sign_in_email_and_token_have_length_limits()
    {
        var longEmail = new string('a', 250) + "@x.co";
        Assert.False(_signIn.Validate(new SignInCommand(longEmail, "password", null)).IsValid);
        Assert.False(_signIn.Validate(new SignInCommand("jane@example.com", "password", new string('t', 2049))).IsValid);
        Assert.True(_signIn.Validate(new SignInCommand("jane@example.com", "password", new string('t', 2048))).IsValid);
    }

    [Fact]
    public void Changing_a_password_needs_both_passwords_and_a_code_and_caps_them()
    {
        Assert.True(_change.Validate(new ChangePasswordCommand("old password here", "new password here", "123456")).IsValid);
        Assert.False(_change.Validate(new ChangePasswordCommand("", "new password here", "123456")).IsValid);
        Assert.False(_change.Validate(new ChangePasswordCommand("old password here", "", "123456")).IsValid);
        Assert.False(_change.Validate(new ChangePasswordCommand(new string('a', 129), "new password here", "123456")).IsValid);
        Assert.False(_change.Validate(new ChangePasswordCommand("old password here", new string('a', 129), "123456")).IsValid);
    }
}

public class TwoFactorValidatorsTests
{
    [Theory]
    [InlineData("123456", true)]
    [InlineData("AB3D-EF4G-HJ5K-MN6P", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void A_code_is_required(string code, bool valid)
    {
        Assert.Equal(valid, new TwoFactorCodeCommandValidator().Validate(new TwoFactorCodeCommand(code)).IsValid);
    }

    [Fact]
    public void A_code_is_capped_in_length()
    {
        var validator = new TwoFactorCodeCommandValidator();

        Assert.True(validator.Validate(new TwoFactorCodeCommand(new string('1', TwoFactorCodeRules.MaxLength))).IsValid);
        Assert.False(validator.Validate(new TwoFactorCodeCommand(new string('1', TwoFactorCodeRules.MaxLength + 1))).IsValid);
    }

    [Fact]
    public void Changing_a_password_without_a_code_is_refused()
    {
        var validator = new ChangePasswordCommandValidator();

        Assert.False(validator.Validate(new ChangePasswordCommand("old password here", "new password here", "")).IsValid);
        Assert.False(validator.Validate(new ChangePasswordCommand("old password here", "new password here", new string('1', 33))).IsValid);
    }
}

public class AuditSubjectTests
{
    [Fact]
    public void The_label_is_stable_and_ignores_case_and_spacing()
    {
        Assert.Equal(
            AuditSubject.ForAttemptedEmail("jane@example.com"),
            AuditSubject.ForAttemptedEmail("  JANE@Example.com "));
    }

    [Fact]
    public void Different_addresses_get_different_labels()
    {
        Assert.NotEqual(
            AuditSubject.ForAttemptedEmail("jane@example.com"),
            AuditSubject.ForAttemptedEmail("john@example.com"));
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("my-actual-Password-123!")] // someone typed their password into the email box
    public void The_raw_text_is_never_part_of_the_label(string typed)
    {
        var label = AuditSubject.ForAttemptedEmail(typed);

        Assert.StartsWith("email-sha256:", label, StringComparison.Ordinal);
        Assert.DoesNotContain(typed.ToLowerInvariant(), label, StringComparison.Ordinal);
        Assert.True(label.Length < 40);
    }
}
