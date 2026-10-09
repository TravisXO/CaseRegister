using CaseRegister.Domain.Accounts;
using Microsoft.AspNetCore.Identity;

namespace CaseRegister.Infrastructure.Identity;

/// <summary>
/// A member's account. The email address is the username. It lives in Infrastructure because it
/// derives from the ASP.NET Core Identity user; the Domain only supplies the value types it uses.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public ApplicationUser()
    {
        // Version 7, so ids sort by creation time like every other entity.
        Id = Guid.CreateVersion7();
    }

    public string FullName { get; set; } = string.Empty;

    public ZieaNumber ZieaNumber { get; set; } = null!;

    public AccountStatus AccountStatus { get; set; } = AccountStatus.Pending;

    /// <summary>Creates an account with its email stored in normalised form. It starts as Pending unless told otherwise.</summary>
    public static ApplicationUser Create(string email, string fullName, ZieaNumber zieaNumber, AccountStatus status = AccountStatus.Pending)
    {
        var normalised = EmailNormaliser.Normalise(email);

        return new ApplicationUser
        {
            UserName = normalised,
            Email = normalised,
            FullName = fullName.Trim(),
            ZieaNumber = zieaNumber,
            AccountStatus = status,
        };
    }
}
