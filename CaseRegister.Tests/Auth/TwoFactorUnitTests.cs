using System.Text;
using CaseRegister.Application.Auth;

namespace CaseRegister.Tests.Auth;

public class TotpTests
{
    // The key and expected values published in RFC 6238, Appendix B (HMAC-SHA1), trimmed to 6 digits.
    private static readonly byte[] _rfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1_111_111_109L, "081804")]
    [InlineData(1_111_111_111L, "050471")]
    [InlineData(1_234_567_890L, "005924")]
    [InlineData(2_000_000_000L, "279037")]
    [InlineData(20_000_000_000L, "353130")]
    public void It_produces_the_codes_in_the_RFC_6238_test_vectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, Totp.ComputeCode(_rfcKey, unixSeconds / Totp.StepSeconds));
    }

    [Fact]
    public void The_settings_are_the_ones_every_authenticator_app_assumes()
    {
        Assert.Equal(30, Totp.StepSeconds);
        Assert.Equal(6, Totp.Digits);
        Assert.Equal(20, Totp.KeyBytes);
    }

    [Fact]
    public void The_current_step_comes_from_the_clock()
    {
        Assert.Equal(1, Totp.CurrentStep(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(2, Totp.CurrentStep(DateTimeOffset.FromUnixTimeSeconds(60)));
    }

    // --- The window: one step either side, no more -------------------------------------------

    [Fact]
    public void Only_one_step_either_side_is_allowed()
    {
        Assert.Equal(1, Totp.StepsEitherSide);
    }

    [Theory]
    [InlineData(-2, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void A_code_is_accepted_only_within_one_step_of_now(int offset, bool accepted)
    {
        const long now = 1_000;
        var code = Totp.ComputeCode(_rfcKey, now + offset);

        var matched = Totp.TryMatch(_rfcKey, code, now, lastUsedStep: null, out var step);

        Assert.Equal(accepted, matched);
        Assert.Equal(accepted ? now + offset : null, step);
    }

    [Fact]
    public void A_code_for_the_step_it_was_used_in_is_a_replay()
    {
        const long now = 1_000;
        var code = Totp.ComputeCode(_rfcKey, now);

        Assert.True(Totp.TryMatch(_rfcKey, code, now, lastUsedStep: null, out var step));
        Assert.False(Totp.TryMatch(_rfcKey, code, now, lastUsedStep: step, out _));
    }

    [Fact]
    public void Using_a_later_step_makes_every_earlier_code_a_replay()
    {
        const long now = 1_000;

        // The code for the next step was used, so the current and previous steps' codes can never be used again.
        Assert.False(Totp.TryMatch(_rfcKey, Totp.ComputeCode(_rfcKey, now), now, lastUsedStep: now + 1, out _));
        Assert.False(Totp.TryMatch(_rfcKey, Totp.ComputeCode(_rfcKey, now - 1), now, lastUsedStep: now + 1, out _));
        Assert.False(Totp.TryMatch(_rfcKey, Totp.ComputeCode(_rfcKey, now + 1), now, lastUsedStep: now + 1, out _));
    }

    [Fact]
    public void A_code_for_a_step_after_the_last_used_one_is_still_fine()
    {
        const long now = 1_000;

        Assert.True(Totp.TryMatch(_rfcKey, Totp.ComputeCode(_rfcKey, now + 1), now, lastUsedStep: now, out var step));
        Assert.Equal(now + 1, step);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData(" 123456")]
    [InlineData("١٢٣٤٥٦")] // Arabic-Indic digits
    public void Anything_that_is_not_six_ascii_digits_is_refused(string code)
    {
        Assert.False(Totp.LooksLikeTotpCode(code));
        Assert.False(Totp.TryMatch(_rfcKey, code, 1_000, null, out _));
    }

    [Fact]
    public void A_different_key_gives_different_codes()
    {
        var other = Encoding.ASCII.GetBytes("98765432109876543210");

        Assert.NotEqual(Totp.ComputeCode(_rfcKey, 1_000), Totp.ComputeCode(other, 1_000));
        Assert.False(Totp.TryMatch(other, Totp.ComputeCode(_rfcKey, 1_000), 1_000, null, out _));
    }

    // --- The address for the QR code ---------------------------------------------------------

    [Fact]
    public void The_otpauth_address_names_the_issuer_the_account_and_the_key()
    {
        var uri = Totp.BuildOtpAuthUri("CaseRegister", "jane.doe@example.test", "JBSWY3DPEHPK3PXP");

        Assert.StartsWith("otpauth://totp/CaseRegister:jane.doe%40example.test?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=CaseRegister", uri, StringComparison.Ordinal);
        Assert.Contains("algorithm=SHA1", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }
}

public class Base32Tests
{
    // The test vectors in RFC 4648, section 10 (without padding).
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void It_matches_the_RFC_4648_test_vectors(string text, string expected)
    {
        Assert.Equal(expected, Base32.Encode(Encoding.ASCII.GetBytes(text)));
        Assert.Equal(text, Encoding.ASCII.GetString(Base32.Decode(expected)));
    }

    [Fact]
    public void Random_keys_survive_a_round_trip()
    {
        for (var length = 1; length <= 40; length++)
        {
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);

            Assert.Equal(bytes, Base32.Decode(Base32.Encode(bytes)));
        }
    }

    [Fact]
    public void Decoding_ignores_case_and_padding()
    {
        Assert.Equal(Base32.Decode("MZXW6YTB"), Base32.Decode("mzxw6ytb"));
        Assert.Equal(Base32.Decode("MZXW6"), Base32.Decode("MZXW6==="));
    }

    [Theory]
    [InlineData("MZXW1")] // 1 is not in the alphabet
    [InlineData("MZ XW")]
    [InlineData("MZXW!")]
    public void Characters_outside_the_alphabet_are_refused(string text)
    {
        Assert.Throws<FormatException>(() => Base32.Decode(text));
    }

    [Fact]
    public void A_twenty_byte_key_is_thirty_two_characters()
    {
        Assert.Equal(32, Base32.Encode(new byte[20]).Length);
    }
}

public class RecoveryCodesTests
{
    [Fact]
    public void Ten_codes_are_made_and_all_are_different()
    {
        var codes = RecoveryCodes.Generate();

        Assert.Equal(10, codes.Count);
        Assert.Equal(10, RecoveryCodes.Count);
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    [Fact]
    public void Each_code_is_four_groups_of_four_without_look_alike_characters()
    {
        foreach (var code in RecoveryCodes.Generate())
        {
            Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){3}$", code);
            Assert.DoesNotContain(code, character => character is '0' or 'O' or '1' or 'I' or 'L');
        }
    }

    [Fact]
    public void Codes_have_80_bits_so_a_stored_hash_cannot_be_guessed_back()
    {
        // 16 characters from a 31-character alphabet: more than 79 bits.
        Assert.Equal(16, RecoveryCodes.Length);
        Assert.True(RecoveryCodes.Length * Math.Log2(31) > 79);
    }

    [Fact]
    public void Two_runs_never_produce_the_same_set()
    {
        Assert.Empty(RecoveryCodes.Generate().Intersect(RecoveryCodes.Generate()));
    }

    [Theory]
    [InlineData("AB3D-EF4G-HJ5K-MN6P")]
    [InlineData("ab3d-ef4g-hj5k-mn6p")]
    [InlineData("AB3DEF4GHJ5KMN6P")]
    [InlineData("  ab3d ef4g hj5k mn6p ")]
    public void However_it_is_typed_it_is_the_same_code(string typed)
    {
        Assert.True(RecoveryCodes.LooksLikeRecoveryCode(typed));
        Assert.Equal(RecoveryCodes.Hash("AB3D-EF4G-HJ5K-MN6P"), RecoveryCodes.Hash(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456")]
    [InlineData("AB3D-EF4G-HJ5K")]
    [InlineData("AB3D-EF4G-HJ5K-MN6P-QR7S")]
    [InlineData("AB3D-EF4G-HJ5K-MN60")] // a zero is not in the alphabet
    [InlineData("AB3D-EF4G-HJ5K-MN6!")]
    public void Things_that_are_not_recovery_codes_are_not_taken_for_one(string typed)
    {
        Assert.False(RecoveryCodes.LooksLikeRecoveryCode(typed));
    }

    [Fact]
    public void The_stored_value_is_a_sha256_hash_and_not_the_code()
    {
        const string code = "AB3D-EF4G-HJ5K-MN6P";
        var hash = RecoveryCodes.Hash(code);

        Assert.Matches("^[0-9A-F]{64}$", hash);
        Assert.DoesNotContain("AB3D", hash, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(hash, RecoveryCodes.Hash("AB3D-EF4G-HJ5K-MN6Q"));
    }
}
