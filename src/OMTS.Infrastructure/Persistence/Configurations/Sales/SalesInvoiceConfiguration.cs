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
        builder.Property(x => x.Amount).HasPrecision(18, 0);

        // The customer debt (BR-DEBT-02) and the payments of an invoice.
        builder.HasIndex(x => new { x.CustomerId, x.StatusId });
        builder.HasIndex(x => x.InvoiceId);
        builder.ToTable(t => t.HasCheckConstraint("CK_CustomerPayment_Amount", "[Amount] > 0"));
    }
}
