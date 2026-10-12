using System.Globalization;
using System.Text;

namespace CaseRegister.Domain.Agencies;

/// <summary>
/// Compares agency names the way a person would: ignoring case, punctuation and company endings such as
/// "Ltd" or "Limited". It only suggests; whether two names are the same agency is a reviewer's decision.
/// </summary>
public static class AgencyNames
{
    private static readonly HashSet<string> _endings = new(StringComparer.Ordinal)
    {
        "limited", "ltd", "plc", "llp", "llc", "inc", "incorporated", "co", "company", "corp", "corporation",
    };

    public static string Normalise(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var builder = new StringBuilder();
        foreach (var character in name.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture).Replace("&", " and ", StringComparison.Ordinal))
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (character is not ('.' or '\'' or '’'))
            {
                builder.Append(' '); // punctuation separates words; apostrophes and full stops just disappear ("L.T.D." is "ltd")
            }
        }

        var words = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Endings come off the end, as many as there are ("Smith Co Ltd"), but never the whole name.
        while (words.Count > 1 && _endings.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }

    public static bool AreSame(string first, string second)
    {
        var normalised = Normalise(first);

        return normalised.Length > 0 && string.Equals(normalised, Normalise(second), StringComparison.Ordinal);
    }
}
