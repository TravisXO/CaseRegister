namespace CaseRegister.Application.Approvals;

/// <summary>An admin as other admins see them: who, and whether they are able to act.</summary>
public sealed record AdminSummary(Guid UserId, string FullName, bool IsActive, bool TwoFactorOn);

/// <param name="ActiveCount">Approved admins: the ones who can propose and confirm.</param>
/// <param name="Minimum">The floor: access can never drop below this many active admins, except from the command line.</param>
/// <param name="Recommended">Below this many, the list says so: with only two, one admin who is away or locked out leaves nobody to confirm anything.</param>
public sealed record AdminListing(IReadOnlyList<AdminSummary> Admins, int ActiveCount, int Minimum, int Recommended);

/// <summary>A person found by an admin's search: enough to tell two people apart and to name the right account, and no more.</summary>
public sealed record PersonSummary(Guid UserId, string FullName, string Email, string Status, IReadOnlyList<string> Roles);

public interface IAdminDirectory
{
    const int MinimumSearchLength = 3;
    const int MaximumResults = 20;

    Task<AdminListing> ListAdminsAsync(CancellationToken cancellationToken = default);

    /// <summary>Accounts whose name or email contains the text. Nothing at all for a search shorter than three characters.</summary>
    Task<IReadOnlyList<PersonSummary>> FindPeopleAsync(string search, CancellationToken cancellationToken = default);
}
