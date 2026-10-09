using CaseRegister.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Persistence;

// Identity without roles: roles are added when authorisation needs them.
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityUserContext<ApplicationUser, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>The data protection key ring (cookie and token encryption keys), kept in the database.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<AuditLogRecord> AuditLog => Set<AuditLogRecord>();

    public DbSet<LockoutEvent> LockoutEvents => Set<LockoutEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Fuzzy name search (similarity and trigram indexes) relies on pg_trgm.
        builder.HasPostgresExtension("pg_trgm");

        // Identity's tables get plain names instead of asp_net_*.
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");

        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // The shared conventions run last so they see every entity type.
        builder.ApplyEntityConventions();
    }
}
