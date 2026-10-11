using CaseRegister.Domain.Approvals;

namespace CaseRegister.Application.Approvals;

public enum AdminActionOutcome
{
    Done,
    NotAnAdmin,
    NotFound,

    /// <summary>The reason is not acceptable.</summary>
    Invalid,

    /// <summary>Nobody acts on themselves.</summary>
    NotAllowed,

    /// <summary>The target is an admin, so this needs a second admin: propose it instead.</summary>
    NeedsTwoAdmins,

    /// <summary>A rule about the current state forbids it (already suspended, or it would leave fewer than two active admins).</summary>
    Refused,
}

public sealed record AdminActionResult(AdminActionOutcome Outcome, IReadOnlyList<ApprovalViolation>? Problems = null);

/// <summary>
/// Actions that take access away. One admin does them at once, with a reason, and they are logged. When
/// the target is an admin they need a second one, and these methods refuse: that goes through
/// <see cref="IPendingActionService"/>. Granting or restoring access is never here.
/// </summary>
public interface IAccountAdministrationService
{
    /// <summary>Suspends an account and ends its sessions at once.</summary>
    Task<AdminActionResult> SuspendUserAsync(Guid actorId, Guid userId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>Signs a person out everywhere, at once. Their account is untouched.</summary>
    Task<AdminActionResult> EndSessionsAsync(Guid actorId, Guid userId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default);

    /// <summary>Suspends an agency: its members lose every agent capability from their next request.</summary>
    Task<AdminActionResult> SuspendAgencyAsync(Guid actorId, Guid agencyId, string reason, string? remoteIpAddress, CancellationToken cancellationToken = default);
}

/// <summary>Whether an agency is active. Asked on every request that needs an agent capability, so a suspension counts at once.</summary>
public interface IAgencyStatus
{
    Task<bool> IsActiveAsync(Guid agencyId, CancellationToken cancellationToken = default);
}

public sealed record SeedAdminRequest(string Email, string FullName, string Password);

public enum SeedAdminOutcome
{
    Created,

    /// <summary>There are already two active admins, so this command has nothing left to do.</summary>
    EnoughAdmins,

    /// <summary>The account could not be created (the password rules, an email already in use). See the errors.</summary>
    Invalid,
}

public sealed record SeedAdminResult(SeedAdminOutcome Outcome, IReadOnlyList<string> Errors, Guid? UserId = null);

/// <summary>Creates the first admins, from the command line, because the approval process needs two admins to exist before it can run.</summary>
public interface IAdminSeeder
{
    Task<int> CountActiveAdminsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether a password would be accepted for this person, by the same rules as every account. Nothing is created.</summary>
    Task<IReadOnlyList<string>> CheckPasswordAsync(string email, string fullName, string password, CancellationToken cancellationToken = default);

    /// <summary>Creates an approved admin with no authenticator, who has to enrol at first sign-in. Only while fewer than two active admins exist.</summary>
    Task<SeedAdminResult> SeedAsync(SeedAdminRequest request, CancellationToken cancellationToken = default);
}
