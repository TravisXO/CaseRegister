using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CaseRegister.Domain.Accounts;

/// <summary>
/// An agent's registration number. People type it with a prefix and separator ("PFX 884", "PFX884",
/// "PFX-884") or as the bare number ("884"), in any letter case with surrounding spaces ignored. It is
/// stored as the integer 884 and displayed as the prefix, a space and the number.
/// </summary>
/// <remarks>
/// The prefix is a display setting, not part of the number, so it is passed in (it comes from
/// configuration). The Domain does not know what it is.
/// </remarks>
public sealed record AgentRegistrationNumber
{
    public const int MinValue = 1;
    public const int MaxValue = 999_999;

    private static readonly ConcurrentDictionary<string, Regex> _patterns = new(StringComparer.Ordinal);

    private AgentRegistrationNumber(int value) => Value = value;

    /// <summary>The number itself, from <see cref="MinValue"/> to <see cref="MaxValue"/>.</summary>
    public int Value { get; }

    /// <summary>Builds a number from its stored integer. Throws if it is out of range.</summary>
    public static AgentRegistrationNumber FromValue(int value) =>
        value is >= MinValue and <= MaxValue
            ? new AgentRegistrationNumber(value)
            : throw new ArgumentOutOfRangeException(nameof(value), value, $"A registration number is from {MinValue} to {MaxValue}.");

    /// <summary>Parses user input. Throws <see cref="FormatException"/> if it is not a valid number.</summary>
    public static AgentRegistrationNumber Parse(string? input, string displayPrefix) =>
        TryParse(input, displayPrefix, out var number)
            ? number
            : throw new FormatException($"Not a valid registration number. Use a form like {displayPrefix} 884, {displayPrefix}884, {displayPrefix}-884 or 884.");

    public static bool TryParse(
        string? input,
        string displayPrefix,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AgentRegistrationNumber? number)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPrefix);
        number = null;

        if (input is null)
        {
            return false;
        }

        var match = PatternFor(displayPrefix).Match(input.Trim());
        if (!match.Success)
        {
            return false;
        }

        var value = int.Parse(match.Groups["digits"].Value, CultureInfo.InvariantCulture);
        if (value is < MinValue or > MaxValue)
        {
            return false;
        }

        number = new AgentRegistrationNumber(value);
        return true;
    }

    /// <summary>The display form: the prefix, a space and the number, for example "PFX 884".</summary>
    public string Format(string displayPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayPrefix);

        return $"{displayPrefix} {Value.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Just the number. Use <see cref="Format"/> for the display form.</summary>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    // Optional prefix, then at most one space or hyphen, then 1 to 6 digits. [0-9] and not \d, which
    // would also accept digits from other scripts; six digits at most, so nothing can overflow an int.
    private static Regex PatternFor(string displayPrefix) =>
        _patterns.GetOrAdd(
            displayPrefix,
            prefix => new Regex(
                $"^(?:{Regex.Escape(prefix)}[ -]?)?(?<digits>[0-9]{{1,6}})$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)));
}
