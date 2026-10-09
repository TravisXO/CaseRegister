using System.Text.RegularExpressions;

namespace CaseRegister.Domain.Accounts;

/// <summary>
/// A membership number. Typed as "ZIEA 884", "ZIEA884", "ZIEA-884" or just "884" (any letter case,
/// surrounding spaces ignored), stored as the integer 884 and always displayed as "ZIEA 884".
/// </summary>
public sealed partial record ZieaNumber
{
    public const int MinValue = 1;
    public const int MaxValue = 999_999;

    private ZieaNumber(int value) => Value = value;

    /// <summary>The number itself, from <see cref="MinValue"/> to <see cref="MaxValue"/>.</summary>
    public int Value { get; }

    /// <summary>Builds a number from its stored integer. Throws if it is out of range.</summary>
    public static ZieaNumber FromValue(int value) =>
        value is >= MinValue and <= MaxValue
            ? new ZieaNumber(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"A membership number is from {MinValue} to {MaxValue}.");

    /// <summary>Parses user input. Throws <see cref="FormatException"/> if it is not a valid number.</summary>
    public static ZieaNumber Parse(string? input) =>
        TryParse(input, out var number)
            ? number
            : throw new FormatException("Not a valid membership number. Use a form like ZIEA 884, ZIEA884, ZIEA-884 or 884.");

    public static bool TryParse(string? input, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZieaNumber? number)
    {
        number = null;

        if (input is null)
        {
            return false;
        }

        // [0-9] and not \d: \d would also accept digits from other scripts. Up to six digits, so a
        // value past the maximum can never overflow an int.
        var match = InputPattern().Match(input.Trim());
        if (!match.Success)
        {
            return false;
        }

        var value = int.Parse(match.Groups["digits"].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (value is < MinValue or > MaxValue)
        {
            return false;
        }

        number = new ZieaNumber(value);
        return true;
    }

    /// <summary>The display form, for example "ZIEA 884".</summary>
    public override string ToString() => $"ZIEA {Value}";

    // Optional "ZIEA" prefix, then at most one space or hyphen, then 1 to 6 digits.
    [GeneratedRegex("^(?:ZIEA[ -]?)?(?<digits>[0-9]{1,6})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InputPattern();
}
