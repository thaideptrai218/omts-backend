using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Platform;
using OMTS.Domain.Enums;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasOne(x => x.RecipientAccount).WithMany().HasForeignKey(x => x.RecipientAccountId);
        // Rows written before SMS / Zalo OA sendings existed are list notifications.
        builder.Property(x => x.Channel).HasDefaultValue(NotificationChannel.InApp).HasSentinel(NotificationChannel.InApp);
        builder.Property(x => x.Type).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Title).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Body).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.MessageCode).HasMaxLength(Lengths.Code);
        builder.Property(x => x.MessageParams).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.RelatedRecordType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RelatedRecordCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.Property(x => x.ProviderMessageId).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.ProviderErrorCode).HasMaxLength(Lengths.ShortText);
        builder.HasOne(x => x.DeliveryStatus).WithMany().HasForeignKey(x => x.DeliveryStatusId);

        // ACCOUNT-08: the list of one account, newest first, and its unread count.
        builder.HasIndex(x => new { x.RecipientAccountId, x.Channel, x.CreatedAt });
        builder.HasIndex(x => new { x.RecipientAccountId, x.ReadAt });
        builder.HasIndex(x => new { x.RelatedRecordType, x.RelatedRecordId });
    }
}
