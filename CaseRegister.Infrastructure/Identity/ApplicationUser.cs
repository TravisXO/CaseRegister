using CaseRegister.Domain.Accounts;
using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// An account: a member of the association or one of its staff. The email address is the username. It
/// lives in Infrastructure because it derives from the ASP.NET Core Identity user; the Domain only
/// supplies the value types it uses.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        // Version 7, so ids sort by creation time like every other entity.
        Id = Guid.CreateVersion7();
    }

    public string FullName { get; set; } = string.Empty;

    /// <summary>"Membership No." on the certificate. Null for association staff, who are not members.</summary>
    public MembershipNumber? MembershipNumber { get; set; }

    /// <summary>Stored and shown, but access never depends on it. Null for association staff.</summary>
    public MembershipClass? MembershipClass { get; set; }

    /// <summary>The one agency an agent belongs to. Null for association staff, who belong to none.</summary>
    public Guid? AgencyId { get; set; }

    public AccountStatus AccountStatus { get; set; } = AccountStatus.Pending;

    /// <summary>Creates a member's account, with its email stored in normalised form. It starts as Pending unless told otherwise.</summary>
    public static ApplicationUser Create(
        string email,
        string fullName,
        MembershipNumber membershipNumber,
        AccountStatus status = AccountStatus.Pending,
        MembershipClass membershipClass = Domain.Accounts.MembershipClass.Full,
        Guid? agencyId = null)
    {
        var user = CreateStaff(email, fullName, status);
        user.MembershipNumber = membershipNumber;
        user.MembershipClass = membershipClass;
        user.AgencyId = agencyId;

        return user;
    }

    /// <summary>Creates an account for association staff: no membership number or class, and no agency.</summary>
    public static ApplicationUser CreateStaff(string email, string fullName, AccountStatus status = AccountStatus.Pending)
    {
        var normalised = EmailNormaliser.Normalise(email);

        return new ApplicationUser
        {
            UserName = normalised,
            Email = normalised,
            FullName = fullName.Trim(),
            AccountStatus = status,
        };
    }
}
