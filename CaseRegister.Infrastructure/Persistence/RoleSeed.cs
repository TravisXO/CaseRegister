using CaseRegister.Domain.Accounts;

namespace CaseRegister.Infrastructure.Persistence;

/// <summary>
/// The four roles, with fixed ids, written into the database by a migration. They are not created at
/// runtime: the application's database role is not even allowed to insert into the roles table.
/// </summary>
public static class RoleSeed
{
    public static Guid IdOf(Role role) => role switch
    {
        Role.RegisteredAgent => new Guid("01990000-0000-7000-8000-000000000001"),
        Role.AgencyComplianceOfficer => new Guid("01990000-0000-7000-8000-000000000002"),
        Role.CaseReviewer => new Guid("01990000-0000-7000-8000-000000000003"),
        Role.Admin => new Guid("01990000-0000-7000-8000-000000000004"),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>A fixed concurrency stamp, because a seeded row has to be the same every time it is generated.</summary>
    public static string StampOf(Role role) => $"role-seed-{(int)role + 1}";
}
