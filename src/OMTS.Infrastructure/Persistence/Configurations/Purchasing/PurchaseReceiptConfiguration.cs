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

internal sealed class PurchaseReceiptLineConfiguration : IEntityTypeConfiguration<PurchaseReceiptLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReceiptLine> builder)
    {
        builder.HasOne(x => x.Receipt).WithMany(r => r.Lines).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Unit).HasMaxLength(30);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.LineAmount).HasPrecision(18, 0);
        builder.HasThousandVndCheck(nameof(PurchaseReceiptLine.LineAmount));
        builder.Property(x => x.SupplierLotCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.ActualSource).HasMaxLength(Lengths.Address);
    }
}

internal sealed class PurchaseReturnConfiguration : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Receipt).WithMany(r => r.Returns).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(PurchaseReturn.RecordedByAccountId));
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.RefundAmount).HasPrecision(18, 0);
        builder.Property(x => x.DebtReductionAmount).HasPrecision(18, 0);
        builder.HasThousandVndCheck(nameof(PurchaseReturn.RefundAmount));
        builder.HasThousandVndCheck(nameof(PurchaseReturn.DebtReductionAmount));
    }
}

internal sealed class PurchaseReturnLineConfiguration : IEntityTypeConfiguration<PurchaseReturnLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnLine> builder)
    {
        builder.HasOne(x => x.Return).WithMany(r => r.Lines).HasForeignKey(x => x.ReturnId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ReceiptLine).WithMany().HasForeignKey(x => x.ReceiptLineId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Property(x => x.ReturnValue).HasPrecision(18, 0);
        builder.HasThousandVndCheck(nameof(PurchaseReturnLine.ReturnValue));
    }
}

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Receipt).WithMany(r => r.Payments).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(SupplierPayment.RecordedByAccountId));
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.HasThousandVndCheck(nameof(SupplierPayment.Amount));
        builder.Property(x => x.ReferenceCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.SupplierId, x.PaymentDate });
    }
}
