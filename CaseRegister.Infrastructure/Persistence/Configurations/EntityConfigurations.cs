using CaseRegister.Domain.Accounts;
using CaseRegister.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaseRegister.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("users");

        // The user assigns its own version 7 id.
        builder.Property(user => user.Id).ValueGeneratedNever();

        builder.Property(user => user.FullName).HasMaxLength(200).IsRequired();

        // Stored as the integer, shown with the configured prefix. One account per membership number.
        builder.Property(user => user.AgentRegistrationNumber)
            .HasConversion(number => number.Value, value => AgentRegistrationNumber.FromValue(value))
            .HasColumnName("agent_registration_number")
            .IsRequired();
        builder.HasIndex(user => user.AgentRegistrationNumber).IsUnique().HasDatabaseName("ux_users_agent_registration_number");

        // Readable in the database, and robust if the enum is ever reordered.
        builder.Property(user => user.AccountStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Identity checks email uniqueness in code; this makes the database enforce it as well.
        builder.HasIndex(user => user.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
    }
}

internal sealed class AuditLogRecordConfiguration : IEntityTypeConfiguration<AuditLogRecord>
{
    public void Configure(EntityTypeBuilder<AuditLogRecord> builder)
    {
        builder.ToTable("audit_log");

        builder.Property(record => record.Action).HasMaxLength(50).IsRequired();
        builder.Property(record => record.Subject).HasMaxLength(256);
        builder.Property(record => record.IpAddress).HasMaxLength(45);
        builder.Property(record => record.Detail).HasMaxLength(500);

        builder.HasIndex(record => record.CreatedAt);
    }
}

internal sealed class LockoutEventConfiguration : IEntityTypeConfiguration<LockoutEvent>
{
    public void Configure(EntityTypeBuilder<LockoutEvent> builder)
    {
        builder.ToTable("lockout_events");

        // Nothing is hard-deleted, so an account with history cannot be removed from under it.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.UserId, e.CreatedAt });
    }
}
