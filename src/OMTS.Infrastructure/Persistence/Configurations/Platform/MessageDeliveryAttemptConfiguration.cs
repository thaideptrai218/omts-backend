using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class MessageDeliveryAttemptConfiguration : IEntityTypeConfiguration<MessageDeliveryAttempt>
{
    public void Configure(EntityTypeBuilder<MessageDeliveryAttempt> builder)
    {
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.SentByAccount).WithMany().HasForeignKey(x => x.SentByAccountId);
        builder.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId);
        builder.HasOne(x => x.Sale).WithMany().HasForeignKey(x => x.SaleId);
        builder.HasOne(x => x.Adjustment).WithMany().HasForeignKey(x => x.AdjustmentId);
        builder.HasOne(x => x.Publication).WithMany().HasForeignKey(x => x.PublicationId);
        builder.HasOne(x => x.Receipt).WithMany().HasForeignKey(x => x.ReceiptId);
        builder.HasOne(x => x.RetryOfAttempt).WithMany().HasForeignKey(x => x.RetryOfAttemptId);

        builder.Property(x => x.Recipient).HasMaxLength(Lengths.Email);
        builder.Property(x => x.DocumentType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.DocumentVersion).HasMaxLength(Lengths.Code);
        builder.Property(x => x.ProviderMessageId).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ProviderResponseCode).HasMaxLength(Lengths.ShortText);

        // Exactly one of the five source document keys has a value (BR-26).
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_MessageDeliveryAttempt_OneSource",
            ConfigurationHelpers.ExactlyOneNotNull(
                nameof(MessageDeliveryAttempt.OrderId),
                nameof(MessageDeliveryAttempt.SaleId),
                nameof(MessageDeliveryAttempt.AdjustmentId),
                nameof(MessageDeliveryAttempt.PublicationId),
                nameof(MessageDeliveryAttempt.ReceiptId))));
    }
}
