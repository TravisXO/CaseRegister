using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;

namespace CaseRegister.Application.Approvals;

/// <summary>
/// A proposal. Which of the optional fields are needed depends on the type, and no others are allowed:
/// what is proposed must be exactly what the confirmer is shown.
/// </summary>
/// <param name="TargetUserId">The account acted on, for every type except the two agency ones.</param>
/// <param name="TargetAgencyId">The agency acted on, for the agency types.</param>
/// <param name="Role">The role to give or take away (AssignRole, RemoveRole).</param>
/// <param name="CertificateId">The certificate to verify (VerifyPractisingCertificate); the target is its owner.</param>
/// <param name="Reason">Why, in 10 to 500 characters.</param>
/// <param name="ExistingAgencyId">The existing agency an approved applicant joins (ApproveApplication). Exactly one of this and the next.</param>
/// <param name="NewAgencyName">The name of a new agency to create for them (ApproveApplication).</param>
/// <param name="AcknowledgedFlags">Every flag on the application, by name. An approval that leaves one out is refused.</param>
/// <param name="InviteEmail">Who to invite (InviteStaff), with the name and the role.</param>
/// <param name="InviteFullName">The invited person's name (InviteStaff).</param>
public sealed record ProposeActionCommand(
    PendingActionType Type,
    Guid? TargetUserId,
    Guid? TargetAgencyId,
    Role? Role,
    Guid? CertificateId,
    string Reason,
    Guid? ExistingAgencyId = null,
    string? NewAgencyName = null,
    IReadOnlyList<string>? AcknowledgedFlags = null,
    string? InviteEmail = null,
    string? InviteFullName = null);

/// <param name="PayloadHash">The hash of the proposal as the confirmer saw it. If the proposal is not that, it is refused.</param>
/// <param name="TwoFactorCode">A fresh authenticator code (or a recovery code) from the confirmer: a signed-in session is not enough.</param>
public sealed record ConfirmActionCommand(string PayloadHash, string TwoFactorCode);

/// <summary>Why a proposal is being rejected or withdrawn.</summary>
public sealed record DecisionReasonCommand(string Reason);

public sealed record PendingActionView(
    Guid Id,
    PendingActionType Type,
    Guid? TargetUserId,
    Guid? TargetAgencyId,
    string? TargetName,
    string Description,
    string Payload,
    string PayloadHash,
    string Reason,
    PendingActionStatus Status,
    Guid ProposedByUserId,
    string? ProposedByName,
    DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    string? LastRefusalCode,
    string? LastRefusalMessage,
    DateTimeOffset? LastRefusedAt,
    string? TargetEmail = null,
    string? DecidedByName = null);

public enum ProposeOutcome
{
    Proposed,
    NotAnAdmin,

    /// <summary>The parameters or the reason are not acceptable.</summary>
    Invalid,
    TargetNotFound,

    /// <summary>A rule forbids it now: self-targeting, the state of the target, the two-admin minimum. See the problems.</summary>
    Refused,
    AlreadyPending,

    /// <summary>It reduces access and the target is not an admin, so it is done at once with the immediate action, not proposed.</summary>
    NoApprovalNeeded,
}

public sealed record ProposeResult(ProposeOutcome Outcome, PendingActionView? Action = null, IReadOnlyList<ApprovalViolation>? Problems = null);

public enum ConfirmOutcome
{
    /// <summary>Applied. Also the answer to a repeat of the same confirmation: the same result, with no second effect.</summary>
    Confirmed,
    NotAnAdmin,
    NotFound,

    /// <summary>The hash quoted is not the hash of this proposal.</summary>
    HashMismatch,

    /// <summary>Already decided (by someone else), or lapsed.</summary>
    NotPending,
    Expired,

    /// <summary>A people rule: the proposer cannot confirm their own proposal, and nobody decides an action on themselves.</summary>
    NotAllowed,

    /// <summary>A rule about the current state no longer holds. Nothing was done; the proposal stays pending with the reason on it.</summary>
    Refused,

    /// <summary>The two-factor code was wrong.</summary>
    CodeRejected,
    RateLimited,
}

public sealed record ConfirmResult(
    ConfirmOutcome Outcome,
    PendingActionView? Action = null,
    IReadOnlyList<ApprovalViolation>? Problems = null,
    TimeSpan? RetryAfter = null,
    bool WasRepeat = false,
    string? SetupLink = null,
    string? EnrolmentCode = null);

public enum DecisionOutcome
{
    Done,
    NotAnAdmin,
    NotFound,
    NotPending,
    Expired,
    NotAllowed,
    Invalid,
}

public sealed record DecisionResult(DecisionOutcome Outcome, PendingActionView? Action = null, IReadOnlyList<ApprovalViolation>? Problems = null);

/// <summary>
/// The maker-checker engine. One admin proposes, a different admin confirms, and the action is applied
/// only then, exactly as proposed. Every rule is enforced here and in <see cref="ApprovalRules"/>, never in a controller.
/// </summary>
public interface IPendingActionService
{
    Task<ProposeResult> ProposeAsync(Guid proposerId, ProposeActionCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>Proposals still waiting, newest first, with expiry worked out from the time. With <paramref name="includeClosed"/> the decided and lapsed ones too.</summary>
    Task<IReadOnlyList<PendingActionView>> ListAsync(bool includeClosed, CancellationToken cancellationToken = default);

    Task<PendingActionView?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ConfirmResult> ConfirmAsync(Guid id, Guid confirmerId, ConfirmActionCommand command, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>A different admin turns a proposal down. The proposer withdraws instead.</summary>
    Task<DecisionResult> RejectAsync(Guid id, Guid rejecterId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>The proposer takes their own pending proposal back.</summary>
    Task<DecisionResult> WithdrawAsync(Guid id, Guid proposerId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default);
}
