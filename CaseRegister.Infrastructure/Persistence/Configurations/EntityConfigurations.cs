using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Certificates;
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

        // Members only: association staff have no membership number. Stored as the integer, shown with the
        // configured prefix. One account per membership number (several NULLs are fine for the staff).
        builder.Property(user => user.MembershipNumber)
            .HasConversion(number => number!.Value, value => MembershipNumber.FromValue(value))
            .HasColumnName("membership_number");
        builder.HasIndex(user => user.MembershipNumber).IsUnique().HasDatabaseName("ux_users_membership_number");

        builder.Property(user => user.MembershipClass).HasConversion<string>().HasMaxLength(20);

        // Every agent belongs to exactly one agency (one column); staff have none. An agency with people
        // in it cannot be removed from under them.
        builder.HasOne<Agency>().WithMany().HasForeignKey(user => user.AgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(user => user.AgencyId);

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

internal sealed class AgencyConfiguration : IEntityTypeConfiguration<Agency>
{
    public void Configure(EntityTypeBuilder<Agency> builder)
    {
        builder.ToTable("agencies");

        builder.Property(agency => agency.Name).HasMaxLength(Agency.MaxNameLength).IsRequired();
        builder.Property(agency => agency.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
    }
}

internal sealed class PractisingCertificateConfiguration : IEntityTypeConfiguration<PractisingCertificate>
{
    public void Configure(EntityTypeBuilder<PractisingCertificate> builder)
    {
        builder.ToTable("practising_certificates");

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(certificate => certificate.UserId).OnDelete(DeleteBehavior.Restrict);

        // Stored as its text form, "9611/2026". Certificate numbers are issued once, so a number can only appear once.
        builder.Property(certificate => certificate.Number)
            .HasConversion(number => number.ToString(), text => CertificateNumber.Parse(text))
            .HasColumnName("certificate_number")
            .HasMaxLength(11)
            .IsRequired();
        builder.HasIndex(certificate => certificate.Number).IsUnique().HasDatabaseName("ux_practising_certificates_number");

        // Plain dates: a certificate runs from 1 January to 31 December.
        builder.Property(certificate => certificate.ValidFrom).HasColumnType("date");
        builder.Property(certificate => certificate.ValidTo).HasColumnType("date");

        // A member's history, newest first.
        builder.HasIndex(certificate => new { certificate.UserId, certificate.ValidFrom });

        builder.Ignore(certificate => certificate.IsVerified);
    }
}
