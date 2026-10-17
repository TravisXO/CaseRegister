namespace CaseRegister.Application.Approvals;

public enum BreakGlassOutcome
{
    Done,
    NotFound,

    /// <summary>The account is not in a state this action applies to (for example it is not approved).</summary>
    NotApplicable,

    /// <summary>One break-glass action per person per 24 hours.</summary>
    LimitReached,

    /// <summary>A rule forbids it in the current state. The message says which.</summary>
    Refused,
}

/// <param name="EnrolmentCode">Set once, for the operator to hand over. It is not stored anywhere in the clear.</param>
public sealed record BreakGlassResult(BreakGlassOutcome Outcome, string? Message = null, string? EnrolmentCode = null);

/// <summary>Who a break-glass command is about, as the operator is shown them before confirming.</summary>
public sealed record BreakGlassTarget(Guid UserId, string FullName, string Email, bool IsAdmin, bool IsApproved);

/// <summary>
/// Actions run from the command line by whoever holds the database credentials, for when the normal two-admin process cannot work
/// (an admin lost their phone and there is no third admin to confirm). Each one is audited with its reason, rate-limited to one per
/// person per 24 hours, and tells every admin and the person concerned.
/// </summary>
public interface IBreakGlassService
{
    /// <summary>A problem if this process would sign audit entries with a different key from the latest entry, or null. Run before anything is written.</summary>
    Task<string?> CheckAuditKeyAsync(CancellationToken cancellationToken = default);

    Task<BreakGlassTarget?> FindAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Removes the person's two-factor and sessions and makes a one-time enrolment code.</summary>
    Task<BreakGlassResult> ResetTwoFactorAsync(Guid userId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends an admin and ends their sessions, even when that leaves fewer than two active admins. The only way below the floor.
    /// </summary>
    Task<BreakGlassResult> SuspendAdminAsync(Guid userId, string reason, CancellationToken cancellationToken = default);
}
