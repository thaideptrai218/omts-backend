using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.HasIndex(x => x.Name).IsUnique();
        // Whole VND; sales money is not limited to multiples of 1,000 (BR-SALE-06).
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        builder.ToTable(t => t.HasCheckConstraint("CK_Product_UnitPrice_Positive", "[UnitPrice] > 0"));
    }
}
