namespace CaseRegister.Application.Auditing;

public enum AuditAction
{
    SignInSucceeded,
    SignInFailed,
    AccountLockedOut,
    AccountSuspended,
    PasswordChanged,
    PasswordChangeFailed,
    TwoFactorEnrolled,
    TwoFactorFailed,
    RecoveryCodeUsed,
    RecoveryCodesRegenerated,
    SessionRevoked,
    RolesChanged,
    PractisingCertificateAdded,
    PractisingCertificateVerified,

    // Maker-checker: both steps of an approval, and a confirmation that was refused.
    ActionProposed,
    ActionConfirmed,
    ActionRejected,
    ActionWithdrawn,
    ActionExpired,
    ActionRefused,

    // What an admin action did to the account or agency (by approval, or at once).
    UserSuspended,
    UserReactivated,
    SessionsEnded,
    AgencySuspended,
    AgencyReactivated,
    TwoFactorReset,

    // Every run of the seed-admin command, whether or not it created anyone.
    AdminSeeded,
    AdminSeedRefused,

    // Applications and staff invites.
    MemberSignedUp,
    MemberReapplied,
    MemberSignUpIgnored,
    ApplicationFlagged,
    ApplicationApproved,
    ApplicationRejected,
    StaffInviteCreated,
    StaffSetupCompleted,
    StaffSetupFailed,

    // The audit log itself.
    AuditLogRead,
    AuditVerified,
    AuditAnchored,
    RecordViewed,

    // Hardening: notifications, lockout, recovery and break-glass.
    NotificationAcknowledged,
    EnrolmentCodeUsed,
    BreakGlassUsed,
    BreakGlassRefused,
}

/// <param name="Action">What happened.</param>
/// <param name="ActorUserId">The account it happened to or was done by, when known.</param>
/// <param name="Subject">What was acted on, such as the email a sign-in was attempted with.</param>
/// <param name="IpAddress">Where the request came from.</param>
/// <param name="Detail">Short, non-sensitive context. Never a password, token or personal identifier.</param>
/// <param name="TargetType">What kind of thing Subject is (User, Agency, ...). Worked out from the action when left out.</param>
/// <param name="Before">The state before, as an object to be written as JSON. Secrets are stripped and size is capped.</param>
/// <param name="After">The state after.</param>
public sealed record AuditEntry(
    AuditAction Action,
    Guid? ActorUserId = null,
    string? Subject = null,
    string? IpAddress = null,
    string? Detail = null,
    string? TargetType = null,
    object? Before = null,
    object? After = null);

/// <summary>
/// Records security-relevant events. This interface is the stable contract: the implementation is
/// replaced by a tamper-evident, hash-chained one later without callers changing.
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
