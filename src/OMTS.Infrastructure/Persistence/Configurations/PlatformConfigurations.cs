using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;
using OMTS.Infrastructure.Persistence.Seed;

namespace OMTS.Infrastructure.Persistence.Configurations;

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

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasOne(x => x.ActorAccount).WithMany().HasForeignKey(x => x.ActorAccountId);
        builder.Property(x => x.Action).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ViewScope).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => new { x.RecordType, x.RecordId });
        builder.HasIndex(x => x.OccurredAt);
    }
}

internal sealed class StatusConfiguration : IEntityTypeConfiguration<Status>
{
    public void Configure(EntityTypeBuilder<Status> builder)
    {
        // Ids are fixed by the catalog (StatusIds), not generated.
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Group).HasMaxLength(Lengths.Code);
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => new { x.Group, x.Code }).IsUnique();
        builder.Property(x => x.DisplayName).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Meaning).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.DisplayColor).HasMaxLength(Lengths.Phone);

        builder.HasData(StatusCatalogSeed.Statuses);
    }
}

internal sealed class StatusTransitionConfiguration : IEntityTypeConfiguration<StatusTransition>
{
    public void Configure(EntityTypeBuilder<StatusTransition> builder)
    {
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasOne(x => x.FromStatus).WithMany().HasForeignKey(x => x.FromStatusId);
        builder.HasOne(x => x.ToStatus).WithMany().HasForeignKey(x => x.ToStatusId);
        builder.HasIndex(x => new { x.FromStatusId, x.ToStatusId }).IsUnique();
        builder.Property(x => x.UseCases).HasMaxLength(Lengths.Code);
        builder.Property(x => x.AllowedPerformers).HasMaxLength(Lengths.Name);
        builder.Property(x => x.TriggerEvent).HasMaxLength(Lengths.Code);
        builder.Property(x => x.GuardCondition).HasMaxLength(Lengths.Reason);

        builder.HasData(StatusCatalogSeed.Transitions);
    }
}
