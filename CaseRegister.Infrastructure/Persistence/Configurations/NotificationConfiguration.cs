using CaseRegister.Domain.Notifications;
using CaseRegister.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaseRegister.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        // Nothing is hard-deleted, so a person with notifications cannot be removed from under them.
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(notification => notification.RecipientId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(notification => notification.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(notification => notification.Severity).HasConversion<string>().HasMaxLength(10).IsRequired();
        builder.Property(notification => notification.Text).HasMaxLength(Notification.MaxTextLength).IsRequired();
        builder.Property(notification => notification.Link).HasMaxLength(Notification.MaxLinkLength).IsRequired();
        builder.Property(notification => notification.DedupeKey).HasMaxLength(Notification.MaxDedupeKeyLength).IsRequired();

        builder.Property(notification => notification.ReadAt).HasColumnType("timestamp with time zone");
        builder.Property(notification => notification.AcknowledgedAt).HasColumnType("timestamp with time zone");

        builder.Ignore(notification => notification.RequiresAcknowledgement);
        builder.Ignore(notification => notification.IsOpen);

        // The same thing is told once: this is what makes INSERT ... ON CONFLICT DO NOTHING work.
        builder.HasIndex(notification => notification.DedupeKey).IsUnique();

        // The bell: a person's most recent notifications, and which of them are still open.
        builder.HasIndex(notification => new { notification.RecipientId, notification.CreatedAt });
    }
}
