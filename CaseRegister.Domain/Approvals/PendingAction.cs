using System.Security.Cryptography;
using System.Text;
using CaseRegister.Domain.Common;

namespace CaseRegister.Domain.Approvals;

/// <summary>
/// An admin action waiting for a second admin. What is proposed cannot change: the payload and its
/// hash are set once, and the confirmer must quote the hash of what they were shown.
/// </summary>
public sealed class PendingAction : Entity
{
    // For EF Core.
    private PendingAction()
    {
    }

    public PendingActionType Type { get; private set; }

    /// <summary>The account acted on. Exactly one of the two targets is set.</summary>
    public Guid? TargetUserId { get; private set; }

    public Guid? TargetAgencyId { get; private set; }

    /// <summary>For an invite, whose account does not exist yet: the invited address, normalised. Then neither of the other two targets is set.</summary>
    public string? TargetEmail { get; private set; }

    /// <summary>What exactly is proposed beyond the type and target (a role, a certificate), as canonical JSON. Never changed.</summary>
    public string Payload { get; private set; } = "{}";

    /// <summary>SHA-256 over the type, the target and the payload, as lower-case hex. See <see cref="HashOf"/>.</summary>
    public string PayloadHash { get; private set; } = string.Empty;

    public string Reason { get; private set; } = string.Empty;

    public Guid ProposedByUserId { get; private set; }

    public DateTimeOffset ProposedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>As stored. Use <see cref="EffectiveStatus"/>, which also knows about time.</summary>
    public PendingActionStatus Status { get; private set; } = PendingActionStatus.Pending;

    public Guid? DecidedByUserId { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>Why it was rejected or withdrawn.</summary>
    public string? DecisionReason { get; private set; }

    /// <summary>Set when a confirmation was refused because a rule no longer held. The proposal stays pending.</summary>
    public string? LastRefusalCode { get; private set; }

    public string? LastRefusalMessage { get; private set; }

    public DateTimeOffset? LastRefusedAt { get; private set; }

    public static PendingAction Propose(
        PendingActionType type,
        Guid? targetUserId,
        Guid? targetAgencyId,
        string payload,
        string reason,
        Guid proposedByUserId,
        DateTimeOffset now,
        string? targetEmail = null)
    {
        if (new object?[] { targetUserId, targetAgencyId, targetEmail }.Count(target => target is not null) != 1)
        {
            throw new ArgumentException("An action targets exactly one user, one agency or one email address.");
        }

        if (ApprovalRules.CheckReason(reason) is { } problem)
        {
            throw new ArgumentException(problem.Message, nameof(reason));
        }

        return new PendingAction
        {
            Type = type,
            TargetUserId = targetUserId,
            TargetAgencyId = targetAgencyId,
            TargetEmail = targetEmail,
            Payload = payload,
            PayloadHash = HashOf(type, targetUserId, targetAgencyId, payload, targetEmail),
            Reason = reason.Trim(),
            ProposedByUserId = proposedByUserId,
            ProposedAt = now,
            ExpiresAt = now + ApprovalRules.Lifetime,
        };
    }

    /// <summary>
    /// What the confirmer has to quote. It covers the type, the target and the payload, so approving a hash
    /// is approving exactly that action on exactly that account.
    /// </summary>
    public static string HashOf(PendingActionType type, Guid? targetUserId, Guid? targetAgencyId, string payload, string? targetEmail = null)
    {
        var canonical = $"{type}|{targetUserId:D}|{targetAgencyId:D}|{payload}" + (targetEmail is null ? string.Empty : $"|{targetEmail}");

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>The stored hash still matches the stored type, target and payload. False means the row was altered outside the application.</summary>
    public bool IsIntact => string.Equals(PayloadHash, HashOf(Type, TargetUserId, TargetAgencyId, Payload, TargetEmail), StringComparison.Ordinal);

    /// <summary>Expiry is worked out here, from the time, not stored by a background job.</summary>
    public PendingActionStatus EffectiveStatus(DateTimeOffset now) =>
        Status == PendingActionStatus.Pending && now >= ExpiresAt ? PendingActionStatus.Expired : Status;

    public bool HashMatches(string? quoted) =>
        quoted is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(quoted.Trim().ToLowerInvariant()), Encoding.ASCII.GetBytes(PayloadHash));

    public void Confirm(Guid byUserId, DateTimeOffset now) => Decide(PendingActionStatus.Confirmed, byUserId, now, null);

    public void Reject(Guid byUserId, DateTimeOffset now, string reason) => Decide(PendingActionStatus.Rejected, byUserId, now, RequireReason(reason));

    public void Withdraw(Guid byUserId, DateTimeOffset now, string reason)
    {
        if (byUserId != ProposedByUserId)
        {
            throw new InvalidOperationException("Only the proposer can withdraw a proposal.");
        }

        Decide(PendingActionStatus.Withdrawn, byUserId, now, RequireReason(reason));
    }

    /// <summary>Writes down that the time ran out. Dated at the moment it lapsed, not when someone noticed.</summary>
    public void Expire(DateTimeOffset now)
    {
        if (Status != PendingActionStatus.Pending || EffectiveStatus(now) != PendingActionStatus.Expired)
        {
            throw new InvalidOperationException("Only a pending proposal whose time has run out can be marked expired.");
        }

        Status = PendingActionStatus.Expired;
        DecidedAt = ExpiresAt;
    }

    /// <summary>A confirmation was refused because the situation changed. The proposal stays pending, with the reason on it.</summary>
    public void RecordRefusal(ApprovalViolation violation, DateTimeOffset now)
    {
        LastRefusalCode = violation.Code;
        LastRefusalMessage = violation.Message;
        LastRefusedAt = now;
    }

    private void Decide(PendingActionStatus status, Guid byUserId, DateTimeOffset now, string? reason)
    {
        if (EffectiveStatus(now) != PendingActionStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending proposal can be decided.");
        }

        Status = status;
        DecidedByUserId = byUserId;
        DecidedAt = now;
        DecisionReason = reason;
    }

    private static string RequireReason(string reason)
    {
        if (ApprovalRules.CheckReason(reason) is { } problem)
        {
            throw new ArgumentException(problem.Message, nameof(reason));
        }

        return reason.Trim();
    }
}
