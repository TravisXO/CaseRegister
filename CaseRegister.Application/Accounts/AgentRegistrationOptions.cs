using System.Text.RegularExpressions;

namespace CaseRegister.Application.Accounts;

/// <summary>
/// How registration numbers are shown and typed. The prefix is configuration (AgentRegistration:DisplayPrefix),
/// so the product can be re-badged without touching the code.
/// </summary>
public sealed partial class AgentRegistrationOptions
{
    public const string DisplayPrefixKey = "AgentRegistration:DisplayPrefix";
    public const int MaxPrefixLength = 10;

    /// <summary>
    /// Shown before the number, for example "PFX 884". Letters only, so it can be told apart from the
    /// digits. The value lives in appsettings.json and nowhere in the code, so there is one place to change it.
    /// </summary>
    public string DisplayPrefix { get; set; } = string.Empty;

    public static bool IsValidPrefix(string? prefix) =>
        !string.IsNullOrEmpty(prefix) && prefix.Length <= MaxPrefixLength && LettersOnly().IsMatch(prefix);

    [GeneratedRegex("^[A-Za-z]+$")]
    private static partial Regex LettersOnly();
}
