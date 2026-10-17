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

public sealed record SessionResponse(string? UserId, bool TwoFactorComplete);

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
    bool HasCurrentPractisingCertificate);

public sealed record ProposalTarget(Guid? UserId, Guid? AgencyId, string? Email, string? Name);

public sealed record ProposalPerson(Guid UserId, string? Name);

public sealed record ProposalRefusal(string? Code, string? Message, DateTimeOffset? At);

/// <param name="Payload">What the proposal would do, in the form it was hashed in.</param>
/// <param name="PayloadHash">The hash a confirmer sends back, so they approve exactly what they were shown.</param>
/// <param name="SetupLink">Only in the answer to confirming a staff invite, once, and nowhere else.</param>
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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SetupLink = null);

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
