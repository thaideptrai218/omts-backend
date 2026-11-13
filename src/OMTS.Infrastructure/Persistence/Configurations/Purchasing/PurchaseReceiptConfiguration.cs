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
        builder.ReferencesAccount(nameof(PurchaseReceipt.RecordedByAccountId), nameof(PurchaseReceipt.ReceivedByAccountId));
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Discount).HasPrecision(18, 0);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        // PURCHASE-03 searches by period (and status), PURCHASE-04 by supplier.
        builder.HasIndex(x => new { x.ReceiptDate, x.StatusId });
        builder.HasIndex(x => new { x.SupplierId, x.StatusId });
    }
}

internal sealed class PurchaseReceiptLineConfiguration : IEntityTypeConfiguration<PurchaseReceiptLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReceiptLine> builder)
    {
        builder.HasOne(x => x.Receipt).WithMany(r => r.Lines).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        // The seed lot code is the receipt code and the line number (GBR-04).
        builder.HasIndex(x => new { x.ReceiptId, x.LineNumber }).IsUnique();
        builder.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Raft).WithMany(r => r.PurchaseReceiptLines).HasForeignKey(x => x.RaftId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Unit).HasMaxLength(30);
        builder.HasOne(x => x.ItemUnit).WithMany().HasForeignKey(x => x.ItemUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.UnitFactor).HasPrecision(18, 3);
        // Base units: an entered quantity of 2 decimals times a factor of 3 decimals has up to 5.
        builder.Property(x => x.Quantity).HasPrecision(18, 5);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.Discount).HasPrecision(18, 0);
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 3);
        builder.Property(x => x.EnteredUnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.EnteredDiscount).HasPrecision(18, 0);
        // Rounded half up to the dong, not to thousands (BR-PURCHASE-02): PurchaseMoney.RoundHalfUp.
        builder.Property(x => x.LineAmount).HasPrecision(18, 0);
        builder.Property(x => x.SupplierLotCode).HasMaxLength(Lengths.ShortText);
    }
}

internal sealed class PurchaseReturnConfiguration : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Receipt).WithMany(r => r.Returns).HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(PurchaseReturn.RecordedByAccountId));
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Discount).HasPrecision(18, 0);
        // The debt reads the Recorded returns of a supplier.
        builder.HasIndex(x => new { x.SupplierId, x.StatusId });
    }
}

internal sealed class PurchaseReturnLineConfiguration : IEntityTypeConfiguration<PurchaseReturnLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnLine> builder)
    {
        builder.HasOne(x => x.Return).WithMany(r => r.Lines).HasForeignKey(x => x.ReturnId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.ReceiptLine).WithMany().HasForeignKey(x => x.ReceiptLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Unit).HasMaxLength(30);
        builder.HasOne(x => x.ItemUnit).WithMany().HasForeignKey(x => x.ItemUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.UnitFactor).HasPrecision(18, 3);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.Quantity).HasPrecision(18, 5);
        builder.Property(x => x.EnteredQuantity).HasPrecision(18, 3);
        builder.Property(x => x.EnteredUnitPrice).HasPrecision(18, 0);
        // Rounded half up to the dong when recorded (BR-PURCHASE-02), and kept: PM 03/11.
        builder.Property(x => x.ReturnValue).HasPrecision(18, 0);
    }
}

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(SupplierPayment.RecordedByAccountId), nameof(SupplierPayment.PaidByAccountId));
        // Whole VND, without the old thousand rule: BR-PURCHASE-08 only asks for an amount above zero.
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.Property(x => x.DiscountAmount).HasPrecision(18, 0);
        builder.HasOne(x => x.PurchaseReturn).WithMany(r => r.Refunds).HasForeignKey(x => x.PurchaseReturnId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.CashAccount).WithMany().HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.BankReference).HasMaxLength(Lengths.ShortText);
        // Not unique: the Accountant may confirm a second payment with the same reference (MSG-PURCHASE-06).
        builder.HasIndex(x => x.BankReference);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.SupplierId, x.PaymentDate });
    }
}

internal sealed class SupplierPaymentAllocationConfiguration : IEntityTypeConfiguration<SupplierPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentAllocation> builder)
    {
        builder.HasOne(x => x.Payment).WithMany(p => p.Allocations).HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Receipt).WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        // One part per receipt on a payment.
        builder.HasIndex(x => new { x.PaymentId, x.ReceiptId }).IsUnique();
        builder.Property(x => x.Amount).HasPrecision(18, 0);
    }
}

