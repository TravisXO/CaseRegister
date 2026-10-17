using CaseRegister.Application.Approvals;
using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Approvals;
using CaseRegister.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Approvals;

internal sealed class AdminDirectory(AppDbContext db) : IAdminDirectory
{
    /// <summary>With fewer admins than this, the admin list warns.</summary>
    public const int Recommended = 3;

    public async Task<IReadOnlyList<PersonSummary>> FindPeopleAsync(string search, CancellationToken cancellationToken = default)
    {
        var text = search.Trim();
        if (text.Length < IAdminDirectory.MinimumSearchLength)
        {
            return [];
        }

        // Escape LIKE's own characters so the search is for the text typed.
        var pattern = "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";

        var people = await db.Users.AsNoTracking()
            .Where(user => EF.Functions.ILike(user.FullName, pattern) || (user.Email != null && EF.Functions.ILike(user.Email, pattern)))
            .OrderBy(user => user.FullName)
            .Take(IAdminDirectory.MaximumResults)
            .Select(user => new { user.Id, user.FullName, user.Email, user.AccountStatus })
            .ToListAsync(cancellationToken);

        var ids = people.Select(person => person.Id).ToList();
        var roleNames = Enum.GetValues<Role>().ToDictionary(role => RoleSeed.IdOf(role), role => role.ToString());
        var held = await db.UserRoles.AsNoTracking().Where(userRole => ids.Contains(userRole.UserId)).ToListAsync(cancellationToken);

        return [.. people.Select(person => new PersonSummary(
            person.Id,
            person.FullName,
            person.Email ?? string.Empty,
            person.AccountStatus.ToString(),
            [.. held.Where(userRole => userRole.UserId == person.Id).Select(userRole => roleNames[userRole.RoleId]).Order(StringComparer.Ordinal)]))];
    }

    public async Task<AdminListing> ListAdminsAsync(CancellationToken cancellationToken = default)
    {
        var adminRoleId = RoleSeed.IdOf(Role.Admin);

        var rows = await (from userRole in db.UserRoles
                          join user in db.Users on userRole.UserId equals user.Id
                          where userRole.RoleId == adminRoleId
                          orderby user.FullName
                          select new { user.Id, user.FullName, user.AccountStatus }).AsNoTracking().ToListAsync(cancellationToken);

        var withTwoFactor = (await db.Authenticators.AsNoTracking().Where(a => a.EnabledAt != null).Select(a => a.UserId).ToListAsync(cancellationToken)).ToHashSet();
        var admins = rows.Select(row => new AdminSummary(row.Id, row.FullName, row.AccountStatus == AccountStatus.Approved, withTwoFactor.Contains(row.Id))).ToList();

        return new AdminListing(admins, admins.Count(admin => admin.IsActive), ApprovalRules.MinimumActiveAdmins, Recommended);
    }
}
