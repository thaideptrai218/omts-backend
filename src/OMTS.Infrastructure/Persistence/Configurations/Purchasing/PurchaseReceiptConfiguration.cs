using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Purchasing;

internal sealed class PurchaseReceiptConfiguration : IEntityTypeConfiguration<PurchaseReceipt>
{
    public void Configure(EntityTypeBuilder<PurchaseReceipt> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Supplier).WithMany(s => s.Receipts).HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Raft).WithMany(r => r.PurchaseReceipts).HasForeignKey(x => x.RaftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PurchaseReceipt>().WithMany().HasForeignKey(x => x.CopiedFromReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(PurchaseReceipt.RecordedByAccountId));
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.DiscountAmount).HasPrecision(18, 0);
        builder.Property(x => x.AmountPaidNow).HasPrecision(18, 0);
        builder.HasThousandVndCheck(nameof(PurchaseReceipt.DiscountAmount));
        builder.HasThousandVndCheck(nameof(PurchaseReceipt.AmountPaidNow));
        // INP-07 searches by zone and period, and by raft for the raft material cost.
        builder.HasIndex(x => new { x.ZoneId, x.ReceiptDate });
        builder.HasIndex(x => new { x.SupplierId, x.StatusId });
    }
}
