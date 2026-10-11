namespace CaseRegister.Domain.Approvals;

/// <param name="Code">Stable and machine-readable; tests and clients depend on it.</param>
/// <param name="Message">For the admin who asked.</param>
public sealed record ApprovalViolation(string Code, string Message);

/// <summary>
/// The rules of maker-checker, written once. The engine, the immediate actions and the tests all use
/// these; nothing else decides who may propose, confirm or act.
/// </summary>
public static class ApprovalRules
{
    public const string SelfTarget = "SelfTarget";
    public const string SameAsProposer = "SameAsProposer";
    public const string NotPending = "NotPending";
    public const string Expired = "Expired";
    public const string MinimumAdmins = "MinimumAdmins";
    public const string ReasonInvalid = "ReasonInvalid";

    /// <summary>A proposal that is not confirmed within this long lapses.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);

    public const int MinimumReasonLength = 10;
    public const int MaximumReasonLength = 500;

    /// <summary>There must always be at least this many active admins, or maker-checker cannot work.</summary>
    public const int MinimumActiveAdmins = 2;

    /// <summary>Actions that take access away. They are immediate, unless the target is an admin.</summary>
    public static bool ReducesAccess(PendingActionType type) =>
        type is PendingActionType.SuspendUser or PendingActionType.EndSessions or PendingActionType.SuspendAgency;

    /// <summary>
    /// Whether an action needs a second admin. Anything that grants or restores access always does.
    /// Anything that reduces it does only when the target is an admin: nobody gets to remove a peer on their own.
    /// </summary>
    public static bool RequiresTwoAdmins(PendingActionType type, bool targetIsAdmin) =>
        !ReducesAccess(type) || targetIsAdmin;

    /// <summary>Actions that can leave fewer admins than before. Their confirmation is serialised so two cannot both slip under the minimum.</summary>
    public static bool CanReduceAdmins(PendingActionType type) =>
        type is PendingActionType.RemoveRole or PendingActionType.SuspendUser;

    /// <summary>True if losing <paramref name="adminsLost"/> of <paramref name="activeAdmins"/> would break the minimum.</summary>
    public static bool WouldLeaveTooFewAdmins(int activeAdmins, int adminsLost) =>
        adminsLost > 0 && activeAdmins - adminsLost < MinimumActiveAdmins;

    /// <summary>The reason has to be a real sentence, not a click-through: 10 to 500 characters once trimmed.</summary>
    public static ApprovalViolation? CheckReason(string? reason)
    {
        var length = reason?.Trim().Length ?? 0;

        return length is >= MinimumReasonLength and <= MaximumReasonLength
            ? null
            : new ApprovalViolation(ReasonInvalid, $"Give a reason of {MinimumReasonLength} to {MaximumReasonLength} characters.");
    }

    /// <summary>
    /// Nobody acts on themselves. For a user target that is the person; for an agency it is the agency
    /// they belong to (association staff, who run these processes, belong to none).
    /// </summary>
    public static bool IsSelfTarget(Guid actorId, Guid? actorAgencyId, Guid? targetUserId, Guid? targetAgencyId) =>
        (targetUserId is { } user && user == actorId)
        || (targetAgencyId is { } agency && actorAgencyId is { } own && agency == own);

    /// <summary>The rules for making a proposal, or for acting at once.</summary>
    public static IReadOnlyList<ApprovalViolation> CheckProposal(Guid proposerId, Guid? proposerAgencyId, Guid? targetUserId, Guid? targetAgencyId)
    {
        List<ApprovalViolation> violations = [];

        if (IsSelfTarget(proposerId, proposerAgencyId, targetUserId, targetAgencyId))
        {
            violations.Add(new ApprovalViolation(SelfTarget, "You cannot propose an action that targets yourself."));
        }

        return violations;
    }

    /// <summary>A proposal can only be acted on while it is pending and its 72 hours have not run out.</summary>
    public static IReadOnlyList<ApprovalViolation> CheckOpen(PendingAction action, DateTimeOffset now)
    {
        return action.EffectiveStatus(now) switch
        {
            PendingActionStatus.Pending => [],
            PendingActionStatus.Expired => [new ApprovalViolation(Expired, "This proposal has expired. Propose it again if it is still needed.")],
            var status => [new ApprovalViolation(NotPending, $"This proposal is already {status.ToString().ToLowerInvariant()}.")],
        };
    }

    /// <summary>
    /// The rules for deciding a proposal that are about people and time (the current state of the
    /// target is checked separately, because it needs the database). Used for confirming and rejecting.
    /// </summary>
    public static IReadOnlyList<ApprovalViolation> CheckDecision(PendingAction action, Guid deciderId, Guid? deciderAgencyId, DateTimeOffset now)
    {
        List<ApprovalViolation> violations = [.. CheckOpen(action, now)];

        if (deciderId == action.ProposedByUserId)
        {
            violations.Add(new ApprovalViolation(SameAsProposer, "A different admin has to decide a proposal from you."));
        }

        if (IsSelfTarget(deciderId, deciderAgencyId, action.TargetUserId, action.TargetAgencyId))
        {
            violations.Add(new ApprovalViolation(SelfTarget, "You cannot decide an action that targets yourself."));
        }

        return violations;
    }
}
