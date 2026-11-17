using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class SalesReturnConfiguration : IEntityTypeConfiguration<SalesReturn>
{
    public void Configure(EntityTypeBuilder<SalesReturn> builder)
    {
        builder.Ignore(x => x.IsStanding);
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Invoice).WithMany(i => i.Returns).HasForeignKey(x => x.InvoiceId);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(nameof(SalesReturn.RecordedByAccountId));
        builder.ReferencesAccount(nameof(SalesReturn.CancelledByAccountId));
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Value).HasPrecision(18, 0);

        // BR-CLAIM-03: at most one return that is not cancelled per invoice; a Cancelled one leaves the invoice free for a new one.
        builder.HasIndex(x => x.InvoiceId, "UX_SalesReturns_Invoice_Recorded")
            .IsUnique()
            .HasFilter($"[StatusId] = {StatusIds.SalesReturn.Recorded}");
        builder.HasIndex(x => x.InvoiceId);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_SalesReturn_Value_NotNegative", "[Value] >= 0");
            t.HasCheckConstraint(
                "CK_SalesReturn_Cancelled_Has_Reason",
                $"[StatusId] <> {StatusIds.SalesReturn.Cancelled} OR ([CancelReason] IS NOT NULL AND [CancelledAt] IS NOT NULL AND [CancelledByAccountId] IS NOT NULL)");
        });
    }
}

internal sealed class SalesReturnLineConfiguration : IEntityTypeConfiguration<SalesReturnLine>
{
    public void Configure(EntityTypeBuilder<SalesReturnLine> builder)
    {
        builder.HasOne(x => x.Return).WithMany(r => r.Lines).HasForeignKey(x => x.ReturnId);
        builder.HasOne(x => x.HarvestLot).WithMany().HasForeignKey(x => x.HarvestLotId);
        builder.Property(x => x.ReturnedKg).HasPrecision(18, 3);
        builder.Property(x => x.DiscardedKg).HasPrecision(18, 3);

        // Each invoice line once in a return (BR-CLAIM-03); the lot index serves the figures of the lots (BR-HARVEST-07).
        builder.HasIndex(x => new { x.ReturnId, x.InvoiceLineId }).IsUnique();
        builder.HasIndex(x => x.HarvestLotId);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_SalesReturnLine_ReturnedKg_Positive", "[ReturnedKg] > 0");
            t.HasCheckConstraint(
                "CK_SalesReturnLine_Discarded_Matches_Outcome",
                "([Outcome] = 'ThrownAway' AND [DiscardedKg] = [ReturnedKg]) OR ([Outcome] = 'BackForSale' AND [DiscardedKg] = 0)");
        });
    }
}
