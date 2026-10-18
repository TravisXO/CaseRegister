using CaseRegister.Domain.Accounts;

namespace CaseRegister.Application.Auditing;

/// <summary>
/// The keys that turn an email address into the label stored in an audit entry (an HMAC-SHA256, so only someone holding the key can test whether
/// an address is in the log). Like the audit keys, every key ever used stays configured, so an old entry can still be recognised as the one a key
/// made; only the current key makes new labels. Configured as Pseudonym:CurrentKeyId and Pseudonym:Keys:&lt;id&gt; (Pseudonym__Keys__p1 ...), never in code.
/// </summary>
public sealed class PseudonymOptions
{
    public const string SectionName = "Pseudonym";
    public const int MinimumKeyBytes = 32;

    public string CurrentKeyId { get; set; } = string.Empty;

    public Dictionary<string, string> Keys { get; set; } = [];

    public string? Problem()
    {
        if (string.IsNullOrWhiteSpace(CurrentKeyId))
        {
            return "Pseudonym:CurrentKeyId is not configured.";
        }

        if (!Keys.TryGetValue(CurrentKeyId, out var current) || string.IsNullOrWhiteSpace(current))
        {
            return $"Pseudonym:Keys:{CurrentKeyId} is not configured. Set it with 'dotnet user-secrets' or the Pseudonym__Keys__{CurrentKeyId} environment variable.";
        }

        foreach (var (id, encoded) in Keys)
        {
            if (id.Contains(':', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(id))
            {
                return "Pseudonym key ids must be plain names without a colon.";
            }

            try
            {
                if (Convert.FromBase64String(encoded).Length < MinimumKeyBytes)
                {
                    return $"Pseudonym:Keys:{id} must be at least {MinimumKeyBytes} bytes (base64).";
                }
            }
            catch (FormatException)
            {
                return $"Pseudonym:Keys:{id} is not valid base64.";
            }
        }

        return null;
    }
}

/// <summary>Makes the label for an email address that is stored in an audit entry in place of the address.</summary>
public interface IPseudonymiser
{
    string CurrentKeyId { get; }

    /// <summary>The ids of every key that is configured (current and retired).</summary>
    IReadOnlyCollection<string> KeyIds { get; }

    /// <summary>"email-hmac:&lt;key id&gt;:&lt;hex&gt;": the same address gives the same label under one key, and never contains the address.</summary>
    string ForEmail(string? email);
}

/// <summary>What the labels in the audit log look like, and the old form that older entries still carry.</summary>
public static class AuditSubject
{
    /// <summary>The current form: a keyed hash, with the id of the key.</summary>
    public const string EmailHmacPrefix = "email-hmac:";

    /// <summary>The form before the pseudonymisation key existed: a plain hash of the address. Old entries keep it (they cannot be changed); nothing makes it any more.</summary>
    public const string LegacyEmailHashPrefix = "email-sha256:";

    public static bool IsEmailLabel(string? subject) =>
        subject is not null && (subject.StartsWith(EmailHmacPrefix, StringComparison.Ordinal) || subject.StartsWith(LegacyEmailHashPrefix, StringComparison.Ordinal));

    /// <summary>The id of the key a current-form label was made with, or null for the old form (or anything else).</summary>
    public static string? KeyIdOf(string? subject)
    {
        if (subject is null || !subject.StartsWith(EmailHmacPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = subject.Split(':');

        return parts.Length == 3 ? parts[1] : null;
    }
}
