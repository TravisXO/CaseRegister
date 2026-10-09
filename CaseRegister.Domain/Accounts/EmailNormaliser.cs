using System.Globalization;
using System.Text;

namespace CaseRegister.Domain.Accounts;

/// <summary>
/// The one place an email address is normalised: trimmed, Unicode-normalised (so look-alike forms of
/// the same character compare equal) and lower-cased. Every store and lookup goes through this.
/// </summary>
public static class EmailNormaliser
{
    public static string Normalise(string email)
    {
        ArgumentNullException.ThrowIfNull(email);

        return email.Trim().Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>True when the address is already in its normalised form.</summary>
    public static bool IsNormalised(string email) =>
        email is not null && string.Equals(email, Normalise(email), StringComparison.Ordinal);
}
