using System.Security.Cryptography;
using System.Text;

namespace CaseRegister.Application.Auth;

/// <summary>
/// The one-time code that lets a person whose two-factor was reset set it up again. It is shown once to the admin (or the person at the
/// command line) who made it, and handed over in person or by phone; only its hash is kept. Twelve characters from an alphabet without
/// look-alikes (60 bits), valid for 72 hours, usable once.
/// </summary>
public static class EnrolmentCodes
{
    public const int Length = 12;
    private const int GroupSize = 4;
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    /// <summary>A new code in the form it is read out: three groups of four, such as AB3D-EF4G-HJ5K.</summary>
    public static string Generate()
    {
        var characters = RandomNumberGenerator.GetItems<char>(Alphabet, Length);

        return string.Join('-', Enumerable.Range(0, Length / GroupSize).Select(index => new string(characters, index * GroupSize, GroupSize)));
    }

    public static string Normalise(string input) =>
        new([.. input.Where(character => character is not ('-' or ' ')).Select(char.ToUpperInvariant)]);

    public static bool LooksLikeEnrolmentCode(string input)
    {
        var normalised = Normalise(input);

        return normalised.Length == Length && normalised.All(character => Alphabet.Contains(character, StringComparison.Ordinal));
    }

    /// <summary>What is stored. A prefix keeps it apart from a recovery code's hash, so one can never be passed off as the other.</summary>
    public static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("enrolment:" + Normalise(input))));
}
