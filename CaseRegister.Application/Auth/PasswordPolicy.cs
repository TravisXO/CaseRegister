namespace CaseRegister.Application.Auth;

/// <summary>
/// The password rules, in one place. They follow NIST SP 800-63B: length matters, forced character
/// types do not, and a password is checked against known breaches instead.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>Also stops absurdly long input being fed to the slow password hash.</summary>
    public const int MaximumLength = 128;

    public const int RequiredUniqueCharacters = 4;
}
