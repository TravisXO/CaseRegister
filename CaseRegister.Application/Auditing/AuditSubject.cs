using System.Security.Cryptography;
using System.Text;
using CaseRegister.Domain.Accounts;

namespace CaseRegister.Application.Auditing;

public static class AuditSubject
{
    /// <summary>
    /// A stable, non-reversible label for an email a sign-in was attempted with. The raw text is never
    /// stored: people sometimes type their password into the email box, and an unknown address is
    /// whatever a stranger chose to send. Equal addresses give equal labels, so repeated attempts can
    /// still be linked.
    /// </summary>
    public static string ForAttemptedEmail(string? email)
    {
        var normalised = EmailNormaliser.Normalise(email ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));

        return "email-sha256:" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
