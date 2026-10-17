using CaseRegister.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaseRegister.Infrastructure.Persistence.Configurations;

internal sealed class SignInFailureConfiguration : IEntityTypeConfiguration<SignInFailure>
{
    public void Configure(EntityTypeBuilder<SignInFailure> builder)
    {
        builder.ToTable("sign_in_failures");

        // Nothing is hard-deleted, so an account with history cannot be removed from under it.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(failure => failure.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(failure => failure.IpAddress).HasMaxLength(45).IsRequired();
        builder.Property(failure => failure.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(failure => failure.ClearedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(failure => new { failure.UserId, failure.Kind, failure.CreatedAt });
    }
}

internal sealed class SignInLockoutConfiguration : IEntityTypeConfiguration<SignInLockout>
{
    public void Configure(EntityTypeBuilder<SignInLockout> builder)
    {
        builder.ToTable("sign_in_lockouts");

        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(lockout => lockout.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(lockout => lockout.IpAddress).HasMaxLength(45);
        builder.Property(lockout => lockout.LockedUntil).HasColumnType("timestamp with time zone");
        builder.Property(lockout => lockout.ClearedAt).HasColumnType("timestamp with time zone");

        builder.HasIndex(lockout => new { lockout.UserId, lockout.LockedUntil });
    }
}
