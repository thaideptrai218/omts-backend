using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class SeedReceiptLotConfiguration : IEntityTypeConfiguration<SeedReceiptLot>
{
    public void Configure(EntityTypeBuilder<SeedReceiptLot> builder)
    {
        builder.HasOne(x => x.SeedType).WithMany().HasForeignKey(x => x.SeedTypeId);
        builder.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
        // One lot per seed line of a purchase receipt (INP-03).
        builder.HasOne(x => x.PurchaseReceiptLine).WithOne(l => l.SeedLot)
            .HasForeignKey<SeedReceiptLot>(x => x.PurchaseReceiptLineId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.InternalCode).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.InternalCode).IsUnique();
        builder.Property(x => x.SupplierLotCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.ActualSource).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Property(x => x.Unit).HasMaxLength(30);
    }
}
