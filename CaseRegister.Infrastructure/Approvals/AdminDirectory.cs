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
