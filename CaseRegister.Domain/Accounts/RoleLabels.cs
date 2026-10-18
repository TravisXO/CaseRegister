namespace CaseRegister.Domain.Accounts;

/// <summary>The words for roles, in one place: every sentence the server writes that names a role uses these (the pages have the same map in src/lib/roles.ts).</summary>
public static class RoleLabels
{
    public static string For(Role role) => role switch
    {
        Role.RegisteredAgent => "Registered agent",
        Role.AgencyComplianceOfficer => "Agency compliance officer",
        Role.CaseReviewer => "Case reviewer",
        Role.Admin => "Admin",
        _ => role.ToString(),
    };

    /// <summary>The label for a role named by its code (as stored and sent); anything that is not a role comes back as it is.</summary>
    public static string For(string code) => Enum.TryParse<Role>(code, out var role) && Enum.IsDefined(role) ? For(role) : code;
}
