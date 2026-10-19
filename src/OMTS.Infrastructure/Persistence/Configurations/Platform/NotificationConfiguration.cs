using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.HasOne(x => x.RecipientAccount).WithMany().HasForeignKey(x => x.RecipientAccountId);
        builder.Property(x => x.Type).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Title).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Body).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.RelatedRecordType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RelatedRecordCode).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => new { x.RecipientAccountId, x.ReadAt });
    }
}
