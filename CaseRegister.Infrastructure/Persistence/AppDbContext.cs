using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Certificates;
using CaseRegister.Domain.Invites;
using CaseRegister.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>The data protection key ring (cookie and token encryption keys), kept in the database.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<AuditLogRecord> AuditLog => Set<AuditLogRecord>();

    public DbSet<AuditChainHead> AuditChainHeads => Set<AuditChainHead>();

    public DbSet<LockoutEvent> LockoutEvents => Set<LockoutEvent>();

    public DbSet<AuthenticatorRecord> Authenticators => Set<AuthenticatorRecord>();

    public DbSet<RecoveryCodeRecord> RecoveryCodes => Set<RecoveryCodeRecord>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Agency> Agencies => Set<Agency>();

    public DbSet<PendingAction> PendingActions => Set<PendingAction>();

    public DbSet<MemberApplication> MemberApplications => Set<MemberApplication>();

    public DbSet<StaffSetupToken> StaffSetupTokens => Set<StaffSetupToken>();

    public DbSet<PractisingCertificate> PractisingCertificates => Set<PractisingCertificate>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Fuzzy name search (similarity and trigram indexes) relies on pg_trgm.
        builder.HasPostgresExtension("pg_trgm");

        // Identity's tables get plain names instead of asp_net_*.
        builder.Entity<IdentityRole<Guid>>().ToTable("roles");
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        // The roles are data in the migration, not something the running application creates.
        builder.Entity<IdentityRole<Guid>>().HasData(
            Enum.GetValues<Role>().Select(role => new IdentityRole<Guid>
            {
                Id = RoleSeed.IdOf(role),
                Name = role.ToString(),
                NormalizedName = EmailNormaliser.Normalise(role.ToString()), // the same normaliser Identity is configured with
                ConcurrencyStamp = RoleSeed.StampOf(role),
            }));

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // The shared conventions run last so they see every entity type.
        builder.ApplyEntityConventions();
    }
}
