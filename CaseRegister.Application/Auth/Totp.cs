using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CaseRegister.Application.Auth;

/// <summary>
/// Time-based one-time passwords (RFC 6238) with the settings every authenticator app uses by default:
/// HMAC-SHA1, 30-second steps, 6 digits. Nothing here is configurable: changing any of it would stop
/// existing apps working.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;

    /// <summary>
    /// A code is accepted from the step before the current one to the step after it: a little slack
    /// for clocks that are slightly out, and no more. (ASP.NET Core Identity's own check accepts two
    /// steps either side, which is more than needed.)
    /// </summary>
    public const int StepsEitherSide = 1;

    public const int KeyBytes = 20;

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static bool LooksLikeTotpCode(string code) =>
        code.Length == Digits && code.All(char.IsAsciiDigit);

    /// <summary>
    /// Finds the step the code belongs to, if it is a valid code for the current step or one either
    /// side, and later than <paramref name="lastUsedStep"/>. A step that has been used once is never
    /// accepted again, so a code seen by someone else (over a shoulder, or in a log) is worthless.
    /// </summary>
    public static bool TryMatch(
        ReadOnlySpan<byte> key,
        string code,
        long currentStep,
        long? lastUsedStep,
        [NotNullWhen(true)] out long? matchedStep)
    {
        matchedStep = null;

        if (!LooksLikeTotpCode(code))
        {
            return false;
        }

        var candidate = Encoding.ASCII.GetBytes(code);
        long? found = null;

        // Every step is always checked, and compared in constant time, so timing says nothing about which one matched.
        for (var offset = -StepsEitherSide; offset <= StepsEitherSide; offset++)
        {
            var step = currentStep + offset;
            var expected = Encoding.ASCII.GetBytes(ComputeCode(key, step));

            if (CryptographicOperations.FixedTimeEquals(expected, candidate) && (lastUsedStep is null || step > lastUsedStep))
            {
                found = step;
            }
        }

        matchedStep = found;
        return found is not null;
    }

    [SuppressMessage(
        "Security",
        "CA5350:Do not use weak cryptographic algorithms",
        Justification = "RFC 6238 and every authenticator app use HMAC-SHA1 for TOTP. It is a keyed MAC here, where SHA-1's collision weakness does not apply.")]
    public static string ComputeCode(ReadOnlySpan<byte> key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);

        // Dynamic truncation (RFC 4226).
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The otpauth:// address an authenticator app reads from the QR code.</summary>
    public static string BuildOtpAuthUri(string issuer, string account, string base32Key) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
            + $"?secret={base32Key}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}");
}
