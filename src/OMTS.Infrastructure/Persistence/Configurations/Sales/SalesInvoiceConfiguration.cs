using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class SalesInvoiceConfiguration : IEntityTypeConfiguration<SalesInvoice>
{
    public void Configure(EntityTypeBuilder<SalesInvoice> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.PurchaseRequest).WithMany().HasForeignKey(x => x.PurchaseRequestId);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(nameof(SalesInvoice.CreatedByAccountId));

        // Whole VND (BR-SALE-06: no rounding to thousands; a line amount is rounded half up to the dong).
        builder.Property(x => x.DiscountAmount).HasPrecision(18, 0);
        builder.Property(x => x.OtherCharges).HasPrecision(18, 0);
        builder.Property(x => x.Subtotal).HasPrecision(18, 0);
        builder.Property(x => x.PriceCutTotal).HasPrecision(18, 0);
        builder.Property(x => x.Total).HasPrecision(18, 0);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        // One QR code per invoice, and a token opens one page (BR-ORIGIN-01): 22 base64url characters.
        builder.Property(x => x.QrToken).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.QrToken).IsUnique();

        // SALE-09 by period and zone, the customer debt (BR-DEBT-02) and Fulfilled (BR-SALE-01).
        builder.HasIndex(x => new { x.InvoiceDate, x.ZoneId });
        builder.HasIndex(x => new { x.CustomerId, x.StatusId });
        builder.HasIndex(x => x.PurchaseRequestId);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_SalesInvoice_Amounts", "[DiscountAmount] >= 0 AND [OtherCharges] >= 0 AND [Subtotal] >= 0 AND [PriceCutTotal] >= 0");
            t.HasCheckConstraint("CK_SalesInvoice_Total", "[Total] >= 0 AND [Total] = [Subtotal] - [DiscountAmount] + [OtherCharges] - [PriceCutTotal]");
            t.HasCheckConstraint("CK_SalesInvoice_Request_Has_Customer", "[PurchaseRequestId] IS NULL OR [CustomerId] IS NOT NULL");
            t.HasCheckConstraint("CK_SalesInvoice_PayLater_Has_Customer", "[PaymentTerm] <> 'PayLater' OR [CustomerId] IS NOT NULL");
        });
    }
}

internal sealed class SalesInvoiceLineConfiguration : IEntityTypeConfiguration<SalesInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLine> builder)
    {
        builder.HasOne(x => x.Invoice).WithMany(i => i.Lines).HasForeignKey(x => x.InvoiceId);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        builder.HasOne(x => x.HarvestLot).WithMany().HasForeignKey(x => x.HarvestLotId);
        builder.HasIndex(x => new { x.InvoiceId, x.LineNumber }).IsUnique();
        // The sales figures of a lot (BR-HARVEST-07) and whether it is on an invoice (BR-HARVEST-05).
        builder.HasIndex(x => x.HarvestLotId);
        builder.Property(x => x.Kg).HasPrecision(18, 3);
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.ListPrice).HasPrecision(18, 0);
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_SalesInvoiceLine_Kg", "[Kg] > 0");
            t.HasCheckConstraint("CK_SalesInvoiceLine_Prices", "[UnitPrice] > 0 AND [ListPrice] > 0 AND [Amount] >= 0");
        });
    }
}

internal sealed class SalesInvoicePriceCutConfiguration : IEntityTypeConfiguration<SalesInvoicePriceCut>
{
    public void Configure(EntityTypeBuilder<SalesInvoicePriceCut> builder)
    {
        builder.HasOne(x => x.Invoice).WithMany(i => i.PriceCuts).HasForeignKey(x => x.InvoiceId);
        builder.ReferencesAccount(nameof(SalesInvoicePriceCut.EnteredByAccountId));
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.InvoiceId, x.CutDate });
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesInvoicePriceCut_Amount", "[Amount] > 0"));
    }
}

internal sealed class CustomerPaymentConfiguration : IEntityTypeConfiguration<CustomerPayment>
{
    public void Configure(EntityTypeBuilder<CustomerPayment> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.Invoice).WithMany(i => i.Payments).HasForeignKey(x => x.InvoiceId);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(nameof(CustomerPayment.CollectedByAccountId));
        builder.ReferencesAccount(nameof(CustomerPayment.RecordedByAccountId));
        builder.ReferencesAccount(nameof(CustomerPayment.ConfirmedByAccountId));
        builder.ReferencesAccount(nameof(CustomerPayment.CancelledByAccountId));
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.Property(x => x.DiscountAmount).HasPrecision(18, 0);
        builder.HasOne(x => x.CashAccount).WithMany().HasForeignKey(x => x.CashAccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.CashAccountId);
        builder.Property(x => x.BankReference).HasMaxLength(Lengths.ShortText);
        // Not unique: the Accountant may save a second payment with the same reference after MSG-DEBT-05 (BR-DEBT-03).
        builder.HasIndex(x => x.BankReference);
        // GFR-08, AC-07: a transfer whose bank reference is already recorded is not recorded again, also when the service reports it twice at once.
        builder.HasIndex(x => x.BankReference, "IX_CustomerPayments_VietQrBankReference")
            .IsUnique()
            .HasFilter("[Method] = 'VietQrInvoice' AND [BankReference] IS NOT NULL");
        builder.Property(x => x.TransferInvoiceCode).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.TransferInvoiceCode);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);

        // The customer debt (BR-DEBT-02), the payments of an invoice and the payments Awaiting confirmation, oldest first (DEBT-02.2).
        builder.HasIndex(x => new { x.CustomerId, x.StatusId });
        builder.HasIndex(x => x.InvoiceId);
        builder.HasIndex(x => new { x.StatusId, x.PaymentDate });
        // Zero only when the payment is all discount.
        builder.ToTable(t => t.HasCheckConstraint("CK_CustomerPayment_Amount", "[Amount] >= 0 AND [DiscountAmount] >= 0 AND ([Amount] > 0 OR [DiscountAmount] > 0)"));
    }
}

internal sealed class CustomerPaymentAllocationConfiguration : IEntityTypeConfiguration<CustomerPaymentAllocation>
{
    public void Configure(EntityTypeBuilder<CustomerPaymentAllocation> builder)
    {
        builder.HasOne(x => x.Payment).WithMany(p => p.Allocations).HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        // One part per invoice on a payment.
        builder.HasIndex(x => new { x.PaymentId, x.InvoiceId }).IsUnique();
        builder.HasIndex(x => x.InvoiceId);
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.ToTable(t => t.HasCheckConstraint("CK_CustomerPaymentAllocation_Amount", "[Amount] > 0"));
    }
}

internal sealed class CustomerDebtAdjustmentConfiguration : IEntityTypeConfiguration<CustomerDebtAdjustment>
{
    public void Configure(EntityTypeBuilder<CustomerDebtAdjustment> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(CustomerDebtAdjustment.RecordedByAccountId), nameof(CustomerDebtAdjustment.CancelledByAccountId));
        builder.Property(x => x.Amount).HasPrecision(18, 0);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.CustomerId, x.AdjustmentDate });
        builder.ToTable(t => t.HasCheckConstraint("CK_CustomerDebtAdjustment_Amount", "[Amount] <> 0"));
    }
}
