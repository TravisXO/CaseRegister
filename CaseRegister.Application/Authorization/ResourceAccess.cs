namespace CaseRegister.Application.Authorization;

/// <summary>A record that belongs to one user (for example a report its author filed).</summary>
public interface IOwnedResource
{
    Guid OwnerUserId { get; }
}

/// <summary>A record that belongs to one agency.</summary>
public interface IAgencyScopedResource
{
    Guid AgencyId { get; }
}

/// <summary>The rules for "own record" and "same agency", written once and used by every handler.</summary>
public static class ResourceAccess
{
    /// <summary>The record is the user's own. A record that has no owner is nobody's own.</summary>
    public static bool IsOwnRecord(Guid userId, object? resource) =>
        resource is IOwnedResource owned && owned.OwnerUserId == userId;

    /// <summary>The record belongs to the user's agency. A user with no agency shares one with nothing.</summary>
    public static bool IsSameAgency(Guid? userAgencyId, object? resource) =>
        userAgencyId is { } agencyId && resource is IAgencyScopedResource scoped && scoped.AgencyId == agencyId;

    /// <summary>Whether a scope from the permission matrix reaches this particular record.</summary>
    public static bool Reaches(AccessScope scope, Guid userId, Guid? userAgencyId, object? resource) => scope switch
    {
        AccessScope.All => true,
        AccessScope.OwnAgency => IsSameAgency(userAgencyId, resource),
        AccessScope.Own => IsOwnRecord(userId, resource),

        // Nothing about a particular record was asked for.
        AccessScope.Unscoped => true,
        _ => false,
    };
}
