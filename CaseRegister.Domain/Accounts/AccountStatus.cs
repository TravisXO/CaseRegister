namespace CaseRegister.Domain.Accounts;

/// <summary>Where a member's account is in its life. Only <see cref="Approved"/> accounts can sign in.</summary>
public enum AccountStatus
{
    /// <summary>Signed up, waiting for approval by two different admins.</summary>
    Pending,

    /// <summary>Approved: the only status that can sign in.</summary>
    Approved,

    /// <summary>The application was turned down.</summary>
    Rejected,

    /// <summary>Blocked, for example after repeated lockouts. An admin has to reactivate it.</summary>
    Suspended,
}
