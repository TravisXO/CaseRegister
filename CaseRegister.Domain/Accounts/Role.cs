namespace CaseRegister.Domain.Accounts;

public enum Role
{
    /// <summary>A registered member who works for an agency.</summary>
    RegisteredAgent,

    /// <summary>The person at an agency who reads and files its FIC reports. Always also a registered agent there.</summary>
    AgencyComplianceOfficer,

    /// <summary>Association staff who decide conduct cases.</summary>
    CaseReviewer,

    /// <summary>Association staff who run the register. They never decide cases.</summary>
    Admin,
}

public static class RoleExtensions
{
    /// <summary>The roles that belong to an agency's own people.</summary>
    public static bool IsAgentRole(this Role role) => role is Role.RegisteredAgent or Role.AgencyComplianceOfficer;

    /// <summary>The roles held by the association's own staff, who belong to no agency.</summary>
    public static bool IsStaffRole(this Role role) => role is Role.CaseReviewer or Role.Admin;
}
