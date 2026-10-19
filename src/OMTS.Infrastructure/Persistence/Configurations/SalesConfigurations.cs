using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Description).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.SalesUnit).HasMaxLength(30);
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.HasOne(x => x.Cooperative).WithMany(c => c.Customers).HasForeignKey(x => x.CooperativeId);
        builder.HasOne(x => x.Account).WithOne(a => a.Customer).HasForeignKey<Customer>(x => x.AccountId);
        builder.HasOne(x => x.MergedIntoCustomer).WithMany().HasForeignKey(x => x.MergedIntoCustomerId);

        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhoneNumber).HasMaxLength(Lengths.Phone);
        builder.HasIndex(x => x.PhoneNumber);
        builder.Property(x => x.Email).HasMaxLength(Lengths.Email);
        builder.Property(x => x.OtherContacts).HasMaxLength(Lengths.Address);
        builder.Property(x => x.ZaloAccountId).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.ZaloAccountId).IsUnique();
        builder.Property(x => x.DeliveryAddress).HasMaxLength(Lengths.Address);
        builder.Property(x => x.ReceivingContact).HasMaxLength(Lengths.Name);
    }
}

internal sealed class SalesOrderConfiguration : IEntityTypeConfiguration<SalesOrder>
{
    public void Configure(EntityTypeBuilder<SalesOrder> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();

        builder.HasOne(x => x.Customer).WithMany(c => c.Orders).HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.SourceLot).WithMany().HasForeignKey(x => x.SourceLotId);
        builder.HasOne(x => x.SourceReturn).WithMany(r => r.ResaleOrders).HasForeignKey(x => x.SourceReturnId);
        builder.ReferencesAccount(nameof(SalesOrder.RecordedByAccountId));

        builder.Property(x => x.RequestedQuantity).HasPrecision(18, 3);
        builder.Property(x => x.QuantityAllocatedFromReturn).HasPrecision(18, 3);
        builder.Property(x => x.OriginalTerms).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.ChannelEvidence).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.TraderProposedChanges).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.AgreementChannel).HasMaxLength(Lengths.Code);
        builder.Property(x => x.AgreementBasis).HasMaxLength(Lengths.Reason);

        builder.HasIndex(x => new { x.ZoneId, x.StatusId });
        builder.HasIndex(x => new { x.CustomerId, x.StatusId });
    }
}

internal sealed class PortWeighingConfiguration : IEntityTypeConfiguration<PortWeighing>
{
    public void Configure(EntityTypeBuilder<PortWeighing> builder)
    {
        builder.HasOne(x => x.Order).WithMany(o => o.Weighings).HasForeignKey(x => x.OrderId);
        builder.Property(x => x.Port).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ConfirmedKg).HasPrecision(18, 3);
        builder.Property(x => x.BuyerConfirmation).HasMaxLength(Lengths.Reason);
    }
}

internal sealed class PortHandoverConfiguration : IEntityTypeConfiguration<PortHandover>
{
    public void Configure(EntityTypeBuilder<PortHandover> builder)
    {
        builder.HasOne(x => x.Order).WithMany(o => o.Handovers).HasForeignKey(x => x.OrderId);
        builder.HasOne(x => x.Weighing).WithMany().HasForeignKey(x => x.WeighingId);
        builder.HasOne(x => x.PublicationAtHandover).WithMany().HasForeignKey(x => x.PublicationIdAtHandover);
        builder.Property(x => x.DeliveredBy).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ReceivedBy).HasMaxLength(Lengths.Name);
    }
}

internal sealed class SaleTransactionConfiguration : IEntityTypeConfiguration<SaleTransaction>
{
    public void Configure(EntityTypeBuilder<SaleTransaction> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();

        // At most one sale per order (E28).
        builder.HasOne(x => x.Order).WithOne(o => o.Sale).HasForeignKey<SaleTransaction>(x => x.OrderId);
        builder.HasOne(x => x.Lot).WithMany(l => l.Sales).HasForeignKey(x => x.LotId);
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        builder.HasOne(x => x.Weighing).WithMany().HasForeignKey(x => x.WeighingId);
        builder.ReferencesAccount(nameof(SaleTransaction.PriceAgreedByAccountId));

        builder.Property(x => x.SalesReceiptUrl).HasMaxLength(Lengths.Url);
        builder.Property(x => x.AgreementBasis).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.DeduplicationKey).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.DeduplicationKey).IsUnique();
        builder.HasThousandVndCheck(nameof(SaleTransaction.TotalAmount));
    }
}
