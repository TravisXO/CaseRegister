using System.Text.Json;
using System.Text.Json.Serialization;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Notifications;

namespace CaseRegister.Api.Contracts;

// The shapes of what the API answers with, named so the API description (and the types the front end is generated from it)
// say what comes back. They carry exactly the JSON the endpoints have always returned.

public sealed record CsrfResponse(string Token);

/// <param name="Status">"two-factor-required" or "two-factor-enrolment-required" after the password; "signed-in" after the code.</param>
public sealed record StatusResponse(string Status);

/// <param name="Mode">"Demo" is the only mode there is for now: all data is fake.</param>
public sealed record DeploymentResponse(string Mode);

/// <param name="SessionEndsAt">The hard 8-hour limit: however active the person is, the session cannot be extended past this.</param>
/// <param name="IdleTimeoutSeconds">How long without a request ends the session (30 minutes). Any request restarts it.</param>
public sealed record SessionResponse(string? UserId, bool TwoFactorComplete, DateTimeOffset? SessionEndsAt = null, int IdleTimeoutSeconds = 0);

/// <param name="OtpAuthUri">For the QR code.</param>
/// <param name="SharedKey">The same key in base32, to type in by hand.</param>
public sealed record EnrolmentResponse(string OtpAuthUri, string SharedKey);

/// <param name="RecoveryCodes">Shown once, and never again.</param>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record MessageResponse(string Message);

public sealed record CreatedResponse(Guid Id);

/// <param name="IsActive">False while the agency is suspended.</param>
public sealed record AgencyReference(Guid Id, string Name, bool IsActive);

public sealed record ProfileResponse(
    Guid Id,
    string FullName,
    string Email,
    string? MembershipNumber,
    string? MembershipClass,
    AgencyReference? Agency,
    IReadOnlyList<string> Roles,
    bool HasCurrentPractisingCertificate,
    PreviousSignIn? LastSignIn,
    int RecoveryCodesRemaining,
    bool RecoveryCodesLow);

/// <summary>An earlier sign-in: when, and the country and city it came from. Never an address.</summary>
public sealed record PreviousSignIn(DateTimeOffset At, string? Country, string? City);

public sealed record ProposalTarget(Guid? UserId, Guid? AgencyId, string? Email, string? Name);

public sealed record ProposalPerson(Guid UserId, string? Name);

public sealed record ProposalRefusal(string? Code, string? Message, DateTimeOffset? At);

/// <param name="Payload">What the proposal would do, in the form it was hashed in.</param>
/// <param name="PayloadHash">The hash a confirmer sends back, so they approve exactly what they were shown.</param>
/// <param name="SetupLink">Only in the answer to confirming a staff invite, once, and nowhere else.</param>
/// <param name="EnrolmentCode">Only in the answer to confirming a reset of somebody's two-factor, once, and nowhere else.</param>
public sealed record PendingActionResponse(
    Guid Id,
    PendingActionType Type,
    ProposalTarget Target,
    string Description,
    JsonElement Payload,
    string PayloadHash,
    string Reason,
    PendingActionStatus Status,
    ProposalPerson ProposedBy,
    DateTimeOffset ProposedAt,
    DateTimeOffset ExpiresAt,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAt,
    string? DecisionReason,
    ProposalRefusal? LastRefusal,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SetupLink = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EnrolmentCode = null);

public sealed record PersonResponse(Guid Id, string FullName, string Email, string Status, IReadOnlyList<string> Roles);

public sealed record AdminSummaryResponse(Guid Id, string FullName, bool IsActive, bool TwoFactorOn);

/// <param name="Warning">True while there are fewer than <paramref name="Recommended"/> active admins.</param>
public sealed record AdminListResponse(IReadOnlyList<AdminSummaryResponse> Admins, int ActiveCount, int Minimum, int Recommended, bool Warning);

public sealed record AuditEntryResponse(
    long Sequence,
    DateTimeOffset OccurredAt,
    string ActorType,
    Guid? ActorUserId,
    string? ActorRole,
    string Action,
    string? TargetType,
    string? TargetId,
    Guid? AgencyId,
    string? CorrelationId,
    string? IpAddress,
    string? Detail,
    JsonElement? Before,
    JsonElement? After);

public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Entries, int Page, int PageSize, long Total);

/// <summary>One notification, as its reader sees it.</summary>
public sealed record NotificationResponse(Guid Id, NotificationType Type, NotificationSeverity Severity, string Text, string Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, DateTimeOffset? AcknowledgedAt, bool NeedsAcknowledgement);

/// <param name="UnreadCount">How many are still open: unread, or critical and not yet acknowledged. What the bell shows.</param>
public sealed record NotificationListResponse(int UnreadCount, int NeedsAcknowledgementCount, IReadOnlyList<NotificationResponse> Items);
