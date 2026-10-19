using CaseRegister.Domain.Common;

namespace CaseRegister.Domain.Notifications;

/// <summary>How much attention a notification asks for.</summary>
public enum NotificationSeverity
{
    Info,
    Warning,

    /// <summary>Something that must be seen and acknowledged by name: it stays until it is, and it is never deleted before.</summary>
    Critical,
}

/// <summary>What happened. The text a person reads is made from this, in one place (see the application's NotificationTexts).</summary>
public enum NotificationType
{
    // For admins.
    ProposalWaiting,
    ProposalConfirmed,
    ProposalRejected,
    ProposalExpiring,
    ApplicationArrived,
    ImmediateRemoval,
    AccountLockedOut,
    BreakGlass,
    AdminCountLow,

    // For the person it concerns.
    TwoFactorResetOnYou,
    CertificateExpiring,
    CertificateLapsed,
    AgencyStatusChanged,
    RolesChanged,

    // Security notices ("was this you?").
    SecurityPasswordChanged,
    SecurityTwoFactorReset,
    SecurityRecoveryCodesUsed,
    SecurityRecoveryCodesRegenerated,
    SecurityRecoveryCodesLow,
    SecurityNewLocation,
    SecurityWrongSecondFactor,
    SecurityPasskeyAdded,
    SecurityPasskeyRemoved,

    // A nudge, not a notice: an admin without a passkey is asked to add one.
    PasskeyAdvice,
}

/// <summary>
/// One message in a person's inbox. It says what happened in plain words and where to go about it. It never carries another person's email
/// address, ID number or case details, and never more than its reader could already see. The dedupe key (type, recipient, subject and a
/// time window) is unique, so the same thing is told once however many times the code that tells it runs.
/// </summary>
public sealed class Notification : Entity
{
    public const int MaxTextLength = 300;
    public const int MaxLinkLength = 200;
    public const int MaxDedupeKeyLength = 200;

    /// <summary>Notifications are kept for this long, unless they are critical and not yet acknowledged.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    // For EF Core.
    private Notification()
    {
    }

    public Guid RecipientId { get; private set; }

    public NotificationType Type { get; private set; }

    public NotificationSeverity Severity { get; private set; }

    public string Text { get; private set; } = string.Empty;

    /// <summary>A path inside the application, such as /admin/proposals/…: never an address on another site.</summary>
    public string Link { get; private set; } = string.Empty;

    public string DedupeKey { get; private set; } = string.Empty;

    public DateTimeOffset? ReadAt { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public bool RequiresAcknowledgement => Severity == NotificationSeverity.Critical;

    /// <summary>A critical notification is not done with until it is acknowledged; any other is done with once read.</summary>
    public bool IsOpen => RequiresAcknowledgement ? AcknowledgedAt is null : ReadAt is null;

    /// <summary>Why a notification with these details cannot exist. Empty when it can.</summary>
    public static IReadOnlyList<string> Check(string text, string link, string dedupeKey)
    {
        List<string> problems = [];

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            problems.Add($"The text must be 1 to {MaxTextLength} characters.");
        }

        // A relative path only, so a notification can never send anyone to another site.
        if (string.IsNullOrWhiteSpace(link) || link.Length > MaxLinkLength || link[0] != '/' || link.StartsWith("//", StringComparison.Ordinal) || link.Contains('\\', StringComparison.Ordinal) || link.Any(char.IsControl))
        {
            problems.Add("The link must be a path inside the application, starting with a single slash.");
        }

        if (string.IsNullOrWhiteSpace(dedupeKey) || dedupeKey.Length > MaxDedupeKeyLength)
        {
            problems.Add($"The dedupe key must be 1 to {MaxDedupeKeyLength} characters.");
        }

        return problems;
    }

    public static Notification Create(Guid recipientId, NotificationType type, NotificationSeverity severity, string text, string link, string dedupeKey)
    {
        var problems = Check(text, link, dedupeKey);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(' ', problems));
        }

        return new Notification
        {
            RecipientId = recipientId,
            Type = type,
            Severity = severity,
            Text = text,
            Link = link,
            DedupeKey = dedupeKey,
        };
    }

    /// <summary>Marks it as seen. A critical one is not finished by this: it also has to be acknowledged.</summary>
    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    /// <summary>Acknowledges a critical notification, which also marks it read. Anything else cannot be acknowledged.</summary>
    public void Acknowledge(DateTimeOffset now)
    {
        if (!RequiresAcknowledgement)
        {
            throw new InvalidOperationException("Only a critical notification is acknowledged.");
        }

        AcknowledgedAt ??= now;
        ReadAt ??= now;
    }
}
