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

internal sealed class AuthenticatorRecordConfiguration : IEntityTypeConfiguration<AuthenticatorRecord>
{
    public void Configure(EntityTypeBuilder<AuthenticatorRecord> builder)
    {
        builder.ToTable("user_authenticators");

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(record => record.UserId).OnDelete(DeleteBehavior.Restrict);

        // One authenticator per account.
        builder.HasIndex(record => record.UserId).IsUnique();

        builder.Property(record => record.EncryptedKey).IsRequired();
    }
}

internal sealed class RecoveryCodeRecordConfiguration : IEntityTypeConfiguration<RecoveryCodeRecord>
{
    public void Configure(EntityTypeBuilder<RecoveryCodeRecord> builder)
    {
        builder.ToTable("recovery_codes");

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(record => record.UserId).OnDelete(DeleteBehavior.Restrict);

        // SHA-256 as hex: 64 characters.
        builder.Property(record => record.CodeHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(record => new { record.UserId, record.CodeHash }).IsUnique();
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(session => session.SessionHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(session => session.SessionHash).IsUnique();
        builder.HasIndex(session => new { session.UserId, session.CreatedAt });

        builder.Property(session => session.Ticket).IsRequired();
        builder.Property(session => session.IpAddress).HasMaxLength(45);
        builder.Property(session => session.UserAgent).HasMaxLength(200);
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
