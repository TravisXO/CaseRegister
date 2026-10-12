using CaseRegister.Domain.Accounts;
using CaseRegister.Domain.Agencies;
using CaseRegister.Domain.Applications;
using CaseRegister.Domain.Approvals;
using CaseRegister.Domain.Certificates;
using CaseRegister.Domain.Invites;
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

        // One live authenticator per account. A removed one (after an admin's reset) stays as history.
        builder.HasIndex(record => record.UserId).IsUnique().HasFilter("removed_at IS NULL").HasDatabaseName("ux_user_authenticators_live");
        builder.HasQueryFilter(record => record.RemovedAt == null);

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

internal sealed class PendingActionConfiguration : IEntityTypeConfiguration<PendingAction>
{
    public void Configure(EntityTypeBuilder<PendingAction> builder)
    {
        builder.ToTable("pending_actions", table =>
        {
            // An action is aimed at exactly one account, one agency or (for an invite, whose account does not exist yet) one email address.
            table.HasCheckConstraint("ck_pending_actions_one_target", "num_nonnulls(target_user_id, target_agency_id, target_email) = 1");
        });

        builder.Property(action => action.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(action => action.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Nothing is hard-deleted, so people and agencies with proposals about them cannot be removed from under them.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(action => action.TargetUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Agency>().WithMany().HasForeignKey(action => action.TargetAgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(action => action.ProposedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(action => action.DecidedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.Ignore(action => action.IsIntact);

        builder.Property(action => action.TargetEmail).HasMaxLength(254);
        builder.Property(action => action.Payload).HasMaxLength(2000).IsRequired();

        // SHA-256 as hex: 64 characters.
        builder.Property(action => action.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(action => action.Reason).HasMaxLength(ApprovalRules.MaximumReasonLength).IsRequired();
        builder.Property(action => action.DecisionReason).HasMaxLength(ApprovalRules.MaximumReasonLength);
        builder.Property(action => action.LastRefusalCode).HasMaxLength(50);
        builder.Property(action => action.LastRefusalMessage).HasMaxLength(500);

        builder.Property(action => action.ProposedAt).HasColumnType("timestamp with time zone");
        builder.Property(action => action.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(action => action.DecidedAt).HasColumnType("timestamp with time zone");
        builder.Property(action => action.LastRefusedAt).HasColumnType("timestamp with time zone");

        // One pending proposal per action type and target. NULLs count as equal (the other target column is
        // always NULL), and a decided or lapsed proposal no longer counts, so the same thing can be proposed again.
        builder.HasIndex(action => new { action.Type, action.TargetUserId, action.TargetAgencyId, action.TargetEmail })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ux_pending_actions_one_pending_per_target");

        builder.HasIndex(action => new { action.Status, action.ExpiresAt });
    }
}

internal sealed class MemberApplicationConfiguration : IEntityTypeConfiguration<MemberApplication>
{
    public void Configure(EntityTypeBuilder<MemberApplication> builder)
    {
        builder.ToTable("member_applications");

        // One application per applicant. Nothing is deleted: the applicant's account is kept, rejected or not.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(application => application.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(application => application.UserId).IsUnique();

        // A claim, not a fact: there is deliberately no unique index, so two applicants can claim one number and be flagged.
        builder.Property(application => application.MembershipNumber)
            .HasConversion(number => number.Value, value => MembershipNumber.FromValue(value))
            .HasColumnName("membership_number");
        builder.HasIndex(application => application.MembershipNumber);

        builder.Property(application => application.MembershipClass).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(application => application.AgencyName).HasMaxLength(MemberApplication.MaxAgencyNameLength).IsRequired();

        builder.Property(application => application.CertificateNumber)
            .HasConversion(number => number.ToString(), text => CertificateNumber.Parse(text))
            .HasColumnName("certificate_number")
            .HasMaxLength(11)
            .IsRequired();

        // Readable in the database ("Reapplication, DuplicateMembershipNumber").
        builder.Property(application => application.Flags).HasConversion<string>().HasMaxLength(100).IsRequired();
        builder.Property(application => application.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(application => application.DecisionReason).HasMaxLength(500);
        builder.Property(application => application.SubmittedAt).HasColumnType("timestamp with time zone");
        builder.Property(application => application.DecidedAt).HasColumnType("timestamp with time zone");

        builder.HasOne<Agency>().WithMany().HasForeignKey(application => application.ApprovedAgencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(application => application.Status);
    }
}

internal sealed class StaffSetupTokenConfiguration : IEntityTypeConfiguration<StaffSetupToken>
{
    public void Configure(EntityTypeBuilder<StaffSetupToken> builder)
    {
        builder.ToTable("staff_setup_tokens");

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(token => token.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(token => token.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        // SHA-256 as hex: 64 characters. Only the hash is kept, never the token.
        builder.Property(token => token.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(token => token.TokenHash).IsUnique();
        builder.HasIndex(token => token.UserId);

        builder.Property(token => token.ExpiresAt).HasColumnType("timestamp with time zone");
        builder.Property(token => token.UsedAt).HasColumnType("timestamp with time zone");
        builder.Property(token => token.RevokedAt).HasColumnType("timestamp with time zone");
    }
}
