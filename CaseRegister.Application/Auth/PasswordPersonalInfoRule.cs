using System.Globalization;
using CaseRegister.Domain.Accounts;

namespace CaseRegister.Application.Auth;

/// <summary>
/// Rejects passwords that contain the user's own details: their email, a meaningful part of it,
/// any part of their name, or their membership number.
/// </summary>
public static class PasswordPersonalInfoRule
{
    // Short fragments would reject far too many unrelated passwords, so only longer ones count.
    private const int MinEmailFragmentLength = 4;
    private const int MinNameFragmentLength = 3;

    // A membership number of 1 to 3 digits ("7") would match countless passwords. Written with its
    // prefix it always counts; on its own it counts from four digits up.
    private const int MinBareNumberDigits = 4;

    public static bool ContainsPersonalInfo(
        string password,
        string email,
        string fullName,
        MembershipNumber? membershipNumber,
        string displayPrefix)
    {
        ArgumentNullException.ThrowIfNull(password);

        var candidate = password.ToLower(CultureInfo.InvariantCulture);

        return ContainsAny(candidate, EmailFragments(email))
            || ContainsAny(candidate, NameFragments(fullName))
            || (membershipNumber is not null && ContainsNumber(candidate, membershipNumber, displayPrefix));
    }

    private static IEnumerable<string> EmailFragments(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            yield break;
        }

        var normalised = EmailNormaliser.Normalise(email);
        yield return normalised;

        var localPart = normalised.Split('@')[0];
        if (localPart.Length >= MinEmailFragmentLength)
        {
            yield return localPart;
        }

        foreach (var fragment in Split(localPart, MinEmailFragmentLength))
        {
            yield return fragment;
        }
    }

    private static IEnumerable<string> NameFragments(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            yield break;
        }

        var lowered = fullName.Trim().ToLower(CultureInfo.InvariantCulture);

        // The whole name run together ("janedoe") counts as well as each part.
        var joined = new string(lowered.Where(char.IsLetterOrDigit).ToArray());
        if (joined.Length >= MinNameFragmentLength)
        {
            yield return joined;
        }

        foreach (var fragment in Split(lowered, MinNameFragmentLength))
        {
            yield return fragment;
        }
    }

    private static bool ContainsNumber(string candidate, MembershipNumber number, string displayPrefix)
    {
        var digits = number.Value.ToString(CultureInfo.InvariantCulture);
        var prefix = displayPrefix.ToLower(CultureInfo.InvariantCulture);

        string[] prefixed = [$"{prefix}{digits}", $"{prefix} {digits}", $"{prefix}-{digits}"];
        if (ContainsAny(candidate, prefixed))
        {
            return true;
        }

        return digits.Length >= MinBareNumberDigits && candidate.Contains(digits, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Split(string value, int minLength) =>
        value.Split(
                [' ', '.', '_', '-', '+', '\'', ','],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(part => part.Length >= minLength);

    private static bool ContainsAny(string candidate, IEnumerable<string> fragments) =>
        fragments.Any(fragment => fragment.Length > 0 && candidate.Contains(fragment, StringComparison.Ordinal));
}
