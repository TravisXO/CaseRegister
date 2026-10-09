using CaseRegister.Domain.Common;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>Records that an account was locked out, so repeated lockouts can lead to suspension. <see cref="Entity.CreatedAt"/> is when.</summary>
public sealed class LockoutEvent : Entity
{
    public LockoutEvent()
    {
    }

    public LockoutEvent(Guid userId) => UserId = userId;

    public Guid UserId { get; private set; }
}
