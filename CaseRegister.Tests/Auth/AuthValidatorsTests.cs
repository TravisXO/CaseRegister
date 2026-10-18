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
    private static readonly string _key1 = Convert.ToBase64String(Enumerable.Range(1, 32).Select(index => (byte)index).ToArray());
    private static readonly string _key2 = Convert.ToBase64String(Enumerable.Range(77, 32).Select(index => (byte)index).ToArray());

    private static CaseRegister.Infrastructure.Auditing.Pseudonymiser Pseudonymiser(string current = "p1", params (string Id, string Key)[] keys) =>
        new(Microsoft.Extensions.Options.Options.Create(
            new PseudonymOptions { CurrentKeyId = current, Keys = (keys.Length == 0 ? [("p1", _key1)] : keys).ToDictionary(pair => pair.Id, pair => pair.Key) }));

    [Fact]
    public void The_label_is_stable_and_ignores_case_and_spacing()
    {
        var pseudonyms = Pseudonymiser();

        Assert.Equal(pseudonyms.ForEmail("jane@example.com"), pseudonyms.ForEmail("  JANE@Example.com "));
    }

    [Fact]
    public void Different_addresses_get_different_labels()
    {
        var pseudonyms = Pseudonymiser();

        Assert.NotEqual(pseudonyms.ForEmail("jane@example.com"), pseudonyms.ForEmail("john@example.com"));
    }

    [Theory]
    [InlineData("jane@example.com")]
    [InlineData("my-actual-Password-123!")] // someone typed their password into the email box
    public void The_raw_text_is_never_part_of_the_label(string typed)
    {
        var label = Pseudonymiser().ForEmail(typed);

        Assert.StartsWith("email-hmac:p1:", label, StringComparison.Ordinal);
        Assert.DoesNotContain(typed.ToLowerInvariant(), label, StringComparison.Ordinal);
        Assert.DoesNotContain("@", label, StringComparison.Ordinal);
        Assert.True(label.Length < 60);
    }

    [Fact]
    public void The_label_is_a_keyed_hash_so_it_is_not_the_plain_hash_anyone_could_compute()
    {
        var label = Pseudonymiser().ForEmail("jane@example.com");
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("jane@example.com"))).ToLowerInvariant();

        Assert.DoesNotContain(plain[..16], label, StringComparison.Ordinal);
        Assert.DoesNotContain(plain[..8], label, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_key_gives_a_different_label_and_the_label_says_which_key_made_it()
    {
        var first = Pseudonymiser("p1", ("p1", _key1)).ForEmail("jane@example.com");
        var second = Pseudonymiser("p2", ("p1", _key1), ("p2", _key2)).ForEmail("jane@example.com");

        Assert.NotEqual(first.Split(':')[2], second.Split(':')[2]);
        Assert.Equal("p1", AuditSubject.KeyIdOf(first));
        Assert.Equal("p2", AuditSubject.KeyIdOf(second));
    }

    [Fact]
    public void After_a_rotation_every_old_key_is_still_known_and_only_the_current_one_makes_labels()
    {
        var rotated = Pseudonymiser("p3", ("p1", _key1), ("p2", _key2), ("p3", Convert.ToBase64String(Enumerable.Range(150, 32).Select(index => (byte)index).ToArray())));

        Assert.Equal(["p1", "p2", "p3"], rotated.KeyIds.Order(StringComparer.Ordinal));
        Assert.Equal("p3", rotated.CurrentKeyId);
        Assert.Equal("p3", AuditSubject.KeyIdOf(rotated.ForEmail("jane@example.com")));
    }

    [Fact]
    public void The_viewer_knows_both_the_new_form_and_the_old_plain_hash()
    {
        Assert.True(AuditSubject.IsEmailLabel("email-hmac:p1:0123456789abcdef0123456789abcdef"));
        Assert.True(AuditSubject.IsEmailLabel("email-sha256:aaaaaaaaaaaaaaaa"));
        Assert.False(AuditSubject.IsEmailLabel("3f2504e0-4f89-11d3-9a0c-0305e82c3301"));
        Assert.False(AuditSubject.IsEmailLabel(null));
        Assert.Null(AuditSubject.KeyIdOf("email-sha256:aaaaaaaaaaaaaaaa"));
        Assert.Null(AuditSubject.KeyIdOf("email-hmac:broken"));
    }

    [Fact]
    public void Key_options_need_a_current_key_that_is_present_and_every_key_valid()
    {
        Assert.Contains("CurrentKeyId", new PseudonymOptions().Problem(), StringComparison.Ordinal);
        Assert.Contains("Pseudonym:Keys:p1", new PseudonymOptions { CurrentKeyId = "p1" }.Problem(), StringComparison.Ordinal);
        Assert.Contains("at least 32", new PseudonymOptions { CurrentKeyId = "p1", Keys = { ["p1"] = Convert.ToBase64String(new byte[8]) } }.Problem(), StringComparison.Ordinal);
        Assert.Contains("base64", new PseudonymOptions { CurrentKeyId = "p1", Keys = { ["p1"] = _key1, ["p0"] = "not base64!" } }.Problem(), StringComparison.Ordinal);
        Assert.Null(new PseudonymOptions { CurrentKeyId = "p1", Keys = { ["p1"] = _key1 } }.Problem());
    }
}
