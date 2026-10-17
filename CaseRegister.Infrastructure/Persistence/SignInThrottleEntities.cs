using CaseRegister.Domain.Common;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>Which factor a failed attempt was against.</summary>
public enum FailureKind
{
    Password,
    SecondFactor,
}

/// <summary>
/// One wrong password or wrong second-factor code, and where it came from. Failures are counted per account and per account and address,
/// so one place guessing at an account cannot lock everyone else out of it. A successful full sign-in clears an account's failures
/// (<see cref="ClearedAt"/>): they are kept, not deleted.
/// </summary>
public sealed class SignInFailure : Entity
{
    public SignInFailure()
    {
    }

    public SignInFailure(Guid userId, string ipAddress, FailureKind kind)
    {
        UserId = userId;
        IpAddress = ipAddress;
        Kind = kind;
    }

    public Guid UserId { get; private set; }

    /// <summary>Where it came from, as the application saw it. "unknown" when it could not be told.</summary>
    public string IpAddress { get; private set; } = string.Empty;

    public FailureKind Kind { get; private set; }

    public DateTimeOffset? ClearedAt { get; set; }
}

/// <summary>
/// A lockout of one address for one account (<see cref="IpAddress"/> is set), or of the whole account (null: only when wrong passwords came
/// from three or more addresses). It ends by itself at <see cref="LockedUntil"/>. Nobody's status changes because of it.
/// </summary>
public sealed class SignInLockout : Entity
{
    public SignInLockout()
    {
    }

    public SignInLockout(Guid userId, string? ipAddress, int level, DateTimeOffset lockedUntil)
    {
        UserId = userId;
        IpAddress = ipAddress;
        Level = level;
        LockedUntil = lockedUntil;
    }

    public Guid UserId { get; private set; }

    /// <summary>The address that is locked out; null when the whole account is.</summary>
    public string? IpAddress { get; private set; }

    /// <summary>1 for the first lockout in 24 hours, 2 for the second, 3 and up for the rest: 15 minutes, 1 hour, 4 hours.</summary>
    public int Level { get; private set; }

    public DateTimeOffset LockedUntil { get; private set; }

    public DateTimeOffset? ClearedAt { get; set; }
}
