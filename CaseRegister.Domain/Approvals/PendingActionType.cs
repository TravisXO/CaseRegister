namespace CaseRegister.Domain.Approvals;

/// <summary>
/// Everything an admin can do to another account or agency that goes through this engine. Whether it
/// needs a second admin is decided in one place, <see cref="ApprovalRules.RequiresTwoAdmins"/>.
/// </summary>
public enum PendingActionType
{
    // Grant or restore access: always two admins.
    AssignRole,
    RemoveRole,
    VerifyPractisingCertificate,
    ReactivateUser,
    ReactivateAgency,
    ResetTwoFactor,
    ApproveApplication,
    RejectApplication,
    InviteStaff,

    // Reduce access: one admin, immediately, unless the target is an admin.
    SuspendUser,
    EndSessions,
    SuspendAgency,
}

public enum PendingActionStatus
{
    Pending,
    Confirmed,
    Rejected,
    Withdrawn,

    /// <summary>Not confirmed within 72 hours. Worked out from the time when read; written down the next time the row is touched.</summary>
    Expired,
}
