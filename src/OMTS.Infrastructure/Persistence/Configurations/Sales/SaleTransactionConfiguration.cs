using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

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
