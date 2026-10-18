using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Certificates;
using CaseRegister.Domain.Invites;
using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CaseRegister.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>
    /// The last audit entry appended during the attempt now running. If a commit's acknowledgement is lost, this is how the retrying execution
    /// strategy tells whether the transaction really committed (its last audit entry is there) before it would run the work a second time.
    /// </summary>
    internal (long Sequence, string Hash)? LastAuditAppend { get; set; }

    private readonly List<Func<CancellationToken, Task>> _afterCommit = [];
    private bool _anchorRequested;

    /// <summary>
    /// Work to do once the database transaction in progress has committed, and only then: anything outside Postgres (storing an audit anchor)
    /// must not run inside a unit of work that can still be retried or rolled back. The interceptor runs it after the commit and drops it on a rollback.
    /// </summary>
    internal void AfterCommit(Func<CancellationToken, Task> work) => _afterCommit.Add(work);

    /// <summary>Asks for an audit anchor to be stored after the commit. Asking twice in one transaction asks once.</summary>
    internal bool TryRequestAnchor()
    {
        if (_anchorRequested)
        {
            return false;
        }

        _anchorRequested = true;

        return true;
    }

    internal async Task RunAfterCommitAsync(CancellationToken cancellationToken)
    {
        var work = _afterCommit.ToList();
        DropAfterCommit();

        foreach (var item in work)
        {
            await item(cancellationToken);
        }
    }

    internal void DropAfterCommit()
    {
        _afterCommit.Clear();
        _anchorRequested = false;
    }

    /// <summary>The data protection key ring (cookie and token encryption keys), kept in the database.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<AuditLogRecord> AuditLog => Set<AuditLogRecord>();

    public DbSet<AuditChainHead> AuditChainHeads => Set<AuditChainHead>();


    public DbSet<AuthenticatorRecord> Authenticators => Set<AuthenticatorRecord>();

    public DbSet<RecoveryCodeRecord> RecoveryCodes => Set<RecoveryCodeRecord>();

    public DbSet<EnrolmentCodeRecord> EnrolmentCodes => Set<EnrolmentCodeRecord>();

    public DbSet<SignInHistoryEntry> SignInHistory => Set<SignInHistoryEntry>();

    public DbSet<AuditReadMarker> AuditReadMarkers => Set<AuditReadMarker>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Agency> Agencies => Set<Agency>();

    public DbSet<PendingAction> PendingActions => Set<PendingAction>();

    public DbSet<MemberApplication> MemberApplications => Set<MemberApplication>();

    public DbSet<StaffSetupToken> StaffSetupTokens => Set<StaffSetupToken>();

    public DbSet<PractisingCertificate> PractisingCertificates => Set<PractisingCertificate>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<SignInFailure> SignInFailures => Set<SignInFailure>();

    public DbSet<SignInLockout> SignInLockouts => Set<SignInLockout>();

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
