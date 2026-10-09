using System.Globalization;
using System.Text.RegularExpressions;

namespace CaseRegister.Domain.Certificates;

/// <summary>
/// A practising certificate number: digits, a slash and the year, for example "9611/2026". Up to six
/// digits and a four-digit year. The year is the calendar year the certificate runs for.
/// </summary>
public sealed partial record CertificateNumber
{
    public const int MinSequence = 1;
    public const int MaxSequence = 999_999;
    public const int MinYear = 1900;
    public const int MaxYear = 2999;

    private CertificateNumber(int sequence, int year)
    {
        Sequence = sequence;
        Year = year;
    }

    public int Sequence { get; }

    public int Year { get; }

    public static CertificateNumber Parse(string? input) =>
        TryParse(input, out var number) ? number : throw new FormatException("Not a valid certificate number. Use a form like 9611/2026.");

    public static bool TryParse(string? input, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CertificateNumber? number)
    {
        number = null;

        // [0-9], not \d (which accepts other scripts), and anchored: nothing before or after.
        var match = Pattern().Match((input ?? string.Empty).Trim());
        if (!match.Success)
        {
            return false;
        }

        var sequence = int.Parse(match.Groups["sequence"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        if (sequence is < MinSequence or > MaxSequence || year is < MinYear or > MaxYear)
        {
            return false;
        }

        number = new CertificateNumber(sequence, year);
        return true;
    }

    /// <summary>The canonical form, for example "9611/2026" (leading zeros are not kept).</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Sequence}/{Year}");

    [GeneratedRegex("^(?<sequence>[0-9]{1,6})/(?<year>[0-9]{4})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
