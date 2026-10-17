using System.Globalization;
using CaseRegister.Application.Auth;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Notifications;

namespace CaseRegister.Application.Notifications;

/// <summary>One notification to be created. <see cref="DedupeKey"/> makes it happen once.</summary>
public sealed record NewNotification(Guid RecipientId, NotificationType Type, NotificationSeverity Severity, string Text, string Link, string DedupeKey);

public sealed record NotificationView(
    Guid Id,
    NotificationType Type,
    NotificationSeverity Severity,
    string Text,
    string Link,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? AcknowledgedAt,
    bool NeedsAcknowledgement);

/// <param name="UnreadCount">How many are still open: unread, or (for a critical one) not yet acknowledged. What the bell shows.</param>
/// <param name="NeedsAcknowledgementCount">How many critical ones are waiting to be acknowledged.</param>
public sealed record NotificationList(int UnreadCount, int NeedsAcknowledgementCount, IReadOnlyList<NotificationView> Items);

public enum NotificationOutcome
{
    Done,

    /// <summary>There is no such notification for this person. Somebody else's looks exactly like one that does not exist.</summary>
    NotFound,

    /// <summary>Only a critical notification is acknowledged.</summary>
    NotCritical,
}

/// <summary>
/// The one place notifications are made and read. Making them never blocks or fails what caused them: <see cref="NotifyAsync"/> does not
/// throw, and callers use it after their own work has been committed. Nothing here runs on a timer: the time-based ones (a proposal about
/// to run out, a certificate about to expire or lapsed) are made when the person signs in and when the bell is fetched.
/// </summary>
public interface INotificationService
{
    /// <summary>Makes the notifications, each once (the dedupe key). Never throws: a failure is logged and the caller carries on.</summary>
    Task NotifyAsync(IReadOnlyCollection<NewNotification> notifications, CancellationToken cancellationToken = default);

    /// <summary>
    /// What the bell and the notifications page show: first the time-based notifications that are now due are made and the old ones cleared
    /// away (at most 100 rows), then the most recent <paramref name="limit"/> are returned with the counts.
    /// </summary>
    Task<NotificationList> GetAsync(Guid userId, int limit, bool unreadOnly, CancellationToken cancellationToken = default);

    /// <summary>Makes the time-based notifications that are due, without listing anything. Called at sign-in. Never throws.</summary>
    Task EnsureTimeBasedAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<NotificationOutcome> MarkReadAsync(Guid userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Marks every notification read except the critical ones, which wait for their acknowledgement.</summary>
    Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges a critical notification. The acknowledgement is written to the audit log.</summary>
    Task<NotificationOutcome> AcknowledgeAsync(Guid userId, Guid id, string? remoteIpAddress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Every notification's wording, link and dedupe key, written once. The texts name what happened and nothing about anyone else: no email
/// address, no ID number, no case details, no name. A person is told only what they could already see.
/// </summary>
public static class NotificationFactory
{
    private const string Once = "once";

    public static string Key(NotificationType type, Guid recipientId, string subject, string window = Once) =>
        $"{type}:{recipientId:N}:{subject}:{window}";

    private static string Place(ClientLocation? location) => location is null ? string.Empty : $" from {location.Display}";

    /// <summary>What an admin action is, in a few plain words ("suspend an account"). Never names anyone.</summary>
    public static string ActionLabel(PendingActionType type) => type switch
    {
        PendingActionType.AssignRole => "give someone a role",
        PendingActionType.RemoveRole => "take a role away",
        PendingActionType.VerifyPractisingCertificate => "verify a practising certificate",
        PendingActionType.ReactivateUser => "reactivate an account",
        PendingActionType.ReactivateAgency => "reactivate an agency",
        PendingActionType.ResetTwoFactor => "reset someone's two-factor sign-in",
        PendingActionType.SuspendUser => "suspend an account",
        PendingActionType.EndSessions => "end someone's sessions",
        PendingActionType.SuspendAgency => "suspend an agency",
        PendingActionType.ApproveApplication => "approve a membership application",
        PendingActionType.RejectApplication => "reject a membership application",
        PendingActionType.InviteStaff => "invite a member of staff",
        _ => "make a change",
    };

    // --- For admins -----------------------------------------------------------------------------------

    public static NewNotification ProposalWaiting(Guid recipientId, Guid proposalId, PendingActionType type) => new(
        recipientId, NotificationType.ProposalWaiting, NotificationSeverity.Info,
        $"A proposal to {ActionLabel(type)} is waiting for you to confirm or reject it.",
        $"/admin/proposals/{proposalId}", Key(NotificationType.ProposalWaiting, recipientId, proposalId.ToString("N")));

    public static NewNotification ProposalConfirmed(Guid recipientId, Guid proposalId, PendingActionType type) => new(
        recipientId, NotificationType.ProposalConfirmed, NotificationSeverity.Info,
        $"Your proposal to {ActionLabel(type)} was confirmed and has been carried out.",
        $"/admin/proposals/{proposalId}", Key(NotificationType.ProposalConfirmed, recipientId, proposalId.ToString("N")));

    public static NewNotification ProposalRejected(Guid recipientId, Guid proposalId, PendingActionType type) => new(
        recipientId, NotificationType.ProposalRejected, NotificationSeverity.Warning,
        $"Your proposal to {ActionLabel(type)} was rejected. Nothing was done.",
        $"/admin/proposals/{proposalId}", Key(NotificationType.ProposalRejected, recipientId, proposalId.ToString("N")));

    public static NewNotification ProposalExpiring(Guid recipientId, Guid proposalId, PendingActionType type) => new(
        recipientId, NotificationType.ProposalExpiring, NotificationSeverity.Warning,
        $"Your proposal to {ActionLabel(type)} runs out within 24 hours and has not been decided.",
        $"/admin/proposals/{proposalId}", Key(NotificationType.ProposalExpiring, recipientId, proposalId.ToString("N")));

    public static NewNotification ApplicationArrived(Guid recipientId, Guid applicantId, string window) => new(
        recipientId, NotificationType.ApplicationArrived, NotificationSeverity.Info,
        "A new membership application is waiting for review.",
        "/admin/applications", Key(NotificationType.ApplicationArrived, recipientId, applicantId.ToString("N"), window));

    /// <param name="what">What was done, in a few words: "suspended an account", "ended all the sessions of an account".</param>
    public static NewNotification ImmediateRemoval(Guid recipientId, Guid eventId, string what) => new(
        recipientId, NotificationType.ImmediateRemoval, NotificationSeverity.Warning,
        $"Another admin {what}. See the audit log for who and why.",
        "/admin/audit", Key(NotificationType.ImmediateRemoval, recipientId, eventId.ToString("N")));

    public static NewNotification LockoutForAdmin(Guid recipientId, Guid eventId, TimeSpan duration, ClientLocation? place, bool wholeAccount) => new(
        recipientId, NotificationType.AccountLockedOut, NotificationSeverity.Warning,
        wholeAccount
            ? $"An account was locked out for {Duration(duration)} because wrong passwords came from several places."
            : $"Sign-in attempts{Place(place)} on an account were locked out for {Duration(duration)} after repeated wrong passwords. Nobody's status changed.",
        "/admin/audit", Key(NotificationType.AccountLockedOut, recipientId, eventId.ToString("N")));

    public static NewNotification BreakGlassForAdmin(Guid recipientId, Guid eventId, string what) => new(
        recipientId, NotificationType.BreakGlass, NotificationSeverity.Critical,
        $"A break-glass action was run from the command line: {what}. It needs your acknowledgement.",
        "/admin/audit", Key(NotificationType.BreakGlass, recipientId, eventId.ToString("N")));

    // --- For the person it concerns -----------------------------------------------------------------------------

    public static NewNotification LockoutForUser(Guid recipientId, Guid eventId, TimeSpan duration, ClientLocation? place, bool wholeAccount) => new(
        recipientId, NotificationType.AccountLockedOut, NotificationSeverity.Warning,
        wholeAccount
            ? $"Your account was locked out for {Duration(duration)} because wrong passwords came from several places. If that was not you, change your password once you can sign in."
            : $"Sign-in attempts{Place(place)} on your account were locked out for {Duration(duration)} after repeated wrong passwords. If that was not you, you do not need to do anything now; you can still sign in from your usual place.",
        "/notifications", Key(NotificationType.AccountLockedOut, recipientId, eventId.ToString("N")));

    public static NewNotification BreakGlassForTarget(Guid recipientId, Guid eventId, string what) => new(
        recipientId, NotificationType.BreakGlass, NotificationSeverity.Critical,
        $"A break-glass action was run on your account from the command line: {what}. If you did not expect this, tell an admin at once.",
        "/profile", Key(NotificationType.BreakGlass, recipientId, eventId.ToString("N")));

    public static NewNotification TwoFactorResetOnYou(Guid recipientId, Guid proposalId) => new(
        recipientId, NotificationType.TwoFactorResetOnYou, NotificationSeverity.Critical,
        "Your two-factor sign-in was reset by two admins, and all your sessions were ended. If you did not ask for this, tell an admin at once.",
        "/profile", Key(NotificationType.TwoFactorResetOnYou, recipientId, proposalId.ToString("N")));

    public static NewNotification CertificateExpiring(Guid recipientId, Guid certificateId, DateOnly validTo) => new(
        recipientId, NotificationType.CertificateExpiring, NotificationSeverity.Warning,
        $"Your practising certificate expires on {validTo.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}. Add next year's certificate before then; an admin has to verify it.",
        "/profile", Key(NotificationType.CertificateExpiring, recipientId, certificateId.ToString("N")));

    public static NewNotification CertificateLapsed(Guid recipientId, int year) => new(
        recipientId, NotificationType.CertificateLapsed, NotificationSeverity.Warning,
        "Your practising certificate has lapsed. Until you add a current one and an admin verifies it, you cannot use the parts of the register that need one.",
        "/profile", Key(NotificationType.CertificateLapsed, recipientId, "lapsed", year.ToString(CultureInfo.InvariantCulture)));

    public static NewNotification AgencyStatusChanged(Guid recipientId, Guid eventId) => new(
        recipientId, NotificationType.AgencyStatusChanged, NotificationSeverity.Info,
        "The status of your agency has changed. Open your profile to see it.",
        "/profile", Key(NotificationType.AgencyStatusChanged, recipientId, eventId.ToString("N")));

    public static NewNotification RolesChanged(Guid recipientId, Guid eventId) => new(
        recipientId, NotificationType.RolesChanged, NotificationSeverity.Info,
        "Your roles have changed, and you were signed out everywhere. Sign in again to carry on.",
        "/profile", Key(NotificationType.RolesChanged, recipientId, eventId.ToString("N")));

    // --- Security notices: "was this you?" ------------------------------------------------------------------------

    public static NewNotification PasswordChanged(Guid recipientId, Guid eventId, ClientLocation? place) => new(
        recipientId, NotificationType.SecurityPasswordChanged, NotificationSeverity.Warning,
        $"Your password was changed{Place(place)}, and your other sessions were ended. Was this you? If not, tell an admin at once.",
        "/profile", Key(NotificationType.SecurityPasswordChanged, recipientId, eventId.ToString("N")));

    public static NewNotification TwoFactorReset(Guid recipientId, Guid eventId) => new(
        recipientId, NotificationType.SecurityTwoFactorReset, NotificationSeverity.Warning,
        "Your two-factor sign-in was reset. Was this you? If not, tell an admin at once.",
        "/profile", Key(NotificationType.SecurityTwoFactorReset, recipientId, eventId.ToString("N")));

    public static NewNotification RecoveryCodeUsed(Guid recipientId, Guid eventId, ClientLocation? place, int remaining) => new(
        recipientId, NotificationType.SecurityRecoveryCodesUsed, NotificationSeverity.Warning,
        $"A recovery code was used to sign in{Place(place)}. You have {remaining} left. Was this you? If not, change your password and tell an admin.",
        "/profile", Key(NotificationType.SecurityRecoveryCodesUsed, recipientId, eventId.ToString("N")));

    public static NewNotification RecoveryCodesRegenerated(Guid recipientId, Guid eventId, ClientLocation? place) => new(
        recipientId, NotificationType.SecurityRecoveryCodesRegenerated, NotificationSeverity.Warning,
        $"New recovery codes were made{Place(place)}; the old ones no longer work, and your other sessions were ended. Was this you? If not, tell an admin at once.",
        "/profile", Key(NotificationType.SecurityRecoveryCodesRegenerated, recipientId, eventId.ToString("N")));

    public static NewNotification RecoveryCodesLow(Guid recipientId, int remaining, string window) => new(
        recipientId, NotificationType.SecurityRecoveryCodesLow, NotificationSeverity.Warning,
        remaining == 0
            ? "You have no recovery codes left. Make new ones on your profile."
            : $"You have {remaining} recovery code{(remaining == 1 ? string.Empty : "s")} left. Make new ones on your profile.",
        "/profile", Key(NotificationType.SecurityRecoveryCodesLow, recipientId, "low", window));

    public static NewNotification NewLocation(Guid recipientId, Guid eventId, ClientLocation place) => new(
        recipientId, NotificationType.SecurityNewLocation, NotificationSeverity.Warning,
        $"You signed in from {place.Display}, which is new for your account. Was this you? If not, change your password and tell an admin.",
        "/profile", Key(NotificationType.SecurityNewLocation, recipientId, eventId.ToString("N")));

    public static NewNotification WrongSecondFactor(Guid recipientId, Guid eventId, ClientLocation? place) => new(
        recipientId, NotificationType.SecurityWrongSecondFactor, NotificationSeverity.Warning,
        $"Someone entered your correct password{Place(place)} and then a wrong two-factor code. Was this you? If not, your password may be known: change it.",
        "/profile", Key(NotificationType.SecurityWrongSecondFactor, recipientId, eventId.ToString("N")));

    private static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{duration.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} hour{(duration.TotalHours >= 2 ? "s" : string.Empty)}"
            : $"{Math.Ceiling(duration.TotalMinutes).ToString("0", CultureInfo.InvariantCulture)} minutes";
}
