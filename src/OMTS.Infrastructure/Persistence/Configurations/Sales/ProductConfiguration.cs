using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique().HasFilter("[Code] <> N''");
        builder.Property(x => x.Description).HasMaxLength(2000);
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.HasIndex(x => x.Name).IsUnique();
        // Whole VND; sales money is not limited to multiples of 1,000 (BR-SALE-06).
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        // Existing cage catalogue entries have no quoted kg price; 0 means that no kg price has been entered.
        builder.ToTable(t => t.HasCheckConstraint("CK_Product_UnitPrice_Positive", "[UnitPrice] > 0 OR ([Code] <> N'' AND [UnitPrice] = 0)"));
    }
}
