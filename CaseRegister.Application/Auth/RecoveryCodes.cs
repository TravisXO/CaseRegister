using System.Security.Cryptography;
using System.Text;

namespace CaseRegister.Application.Auth;

/// <summary>
/// One-time recovery codes for a member who has lost their authenticator. Sixteen characters from an
/// alphabet without look-alikes (80 bits), so a stored hash cannot be guessed back to a code.
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    public const int Length = 16;
    private const int GroupSize = 4;

    // No 0/O, 1/I/L: a code is read off paper.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>Makes a fresh set, in the form shown to the member: four groups of four, such as AB3D-EF4G-HJ5K-MN6P.</summary>
    public static IReadOnlyList<string> Generate() =>
        [.. Enumerable.Range(0, Count).Select(_ => Format(RandomNumberGenerator.GetItems<char>(Alphabet, Length)))];

    /// <summary>True when the text has the shape of a recovery code, however it was typed (hyphens, spaces and case are ignored).</summary>
    public static bool LooksLikeRecoveryCode(string input)
    {
        var normalised = Normalise(input);

        return normalised.Length == Length && normalised.All(character => Alphabet.Contains(character, StringComparison.Ordinal));
    }

    public static string Normalise(string input) =>
        new([.. input.Where(character => character is not ('-' or ' ')).Select(char.ToUpperInvariant)]);

    /// <summary>The value that is stored: a SHA-256 hash of the normalised code. The code itself is never kept.</summary>
    public static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalise(input))));

    private static string Format(char[] characters)
    {
        var groups = Enumerable.Range(0, characters.Length / GroupSize).Select(index => new string(characters, index * GroupSize, GroupSize));

        return string.Join('-', groups);
    }
}
