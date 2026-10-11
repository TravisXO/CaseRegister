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
}

/// <param name="Action">What happened.</param>
/// <param name="ActorUserId">The account it happened to or was done by, when known.</param>
/// <param name="Subject">What was acted on, such as the email a sign-in was attempted with.</param>
/// <param name="IpAddress">Where the request came from.</param>
/// <param name="Detail">Short, non-sensitive context. Never a password, token or personal identifier.</param>
public sealed record AuditEntry(
    AuditAction Action,
    Guid? ActorUserId = null,
    string? Subject = null,
    string? IpAddress = null,
    string? Detail = null);

/// <summary>
/// Records security-relevant events. This interface is the stable contract: the implementation is
/// replaced by a tamper-evident, hash-chained one later without callers changing.
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
