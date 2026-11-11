using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.HasIndex(x => x.Name).IsUnique();
        // Whole VND; sales money is not limited to multiples of 1,000 (BR-SALE-06).
        builder.Property(x => x.UnitPrice).HasPrecision(18, 0);
        builder.Property(x => x.Description).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.Note).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.MinKg).HasPrecision(18, 3);
        builder.Property(x => x.MaxKg).HasPrecision(18, 3);
        builder.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.CategoryId);
        builder.HasIndex(x => x.IsHidden);
        builder.ToTable(t => t.HasCheckConstraint("CK_Product_UnitPrice_Positive", "[UnitPrice] > 0"));
    }
}

internal sealed class ProductUnitConfiguration : IEntityTypeConfiguration<ProductUnit>
{
    public void Configure(EntityTypeBuilder<ProductUnit> builder)
    {
        builder.HasOne(x => x.Product).WithMany(p => p.Units).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Factor).HasPrecision(18, 3);
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.Property(x => x.SalePrice).HasPrecision(18, 0);
        builder.ToTable(t => t.HasCheckConstraint("CK_ProductUnit_SalePrice_Positive", "[SalePrice] > 0"));
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => new { x.ProductId, x.UnitId }).IsUnique();
        // One base unit per product.
        builder.HasIndex(x => x.ProductId).IsUnique().HasFilter("[IsBase] = 1").HasDatabaseName("IX_ProductUnits_ProductId_Base");
    }
}

internal sealed class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ParentId);
    }
}

internal sealed class ProductPhotoConfiguration : IEntityTypeConfiguration<ProductPhoto>
{
    public void Configure(EntityTypeBuilder<ProductPhoto> builder)
    {
        builder.HasOne(x => x.Product).WithMany(p => p.Photos).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.StorageKey).HasMaxLength(Lengths.Url);
    }
}

internal sealed class ProductPriceChangeConfiguration : IEntityTypeConfiguration<ProductPriceChange>
{
    public void Configure(EntityTypeBuilder<ProductPriceChange> builder)
    {
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.UnitCode).HasMaxLength(Lengths.Code);
        builder.Property(x => x.UnitName).HasMaxLength(30);
        builder.Property(x => x.OldPrice).HasPrecision(18, 0);
        builder.Property(x => x.NewPrice).HasPrecision(18, 0);
        builder.ReferencesAccount(nameof(ProductPriceChange.ChangedByAccountId));
        builder.HasIndex(x => new { x.ProductId, x.ChangedAt });
        builder.HasIndex(x => new { x.ProductId, x.UnitId });
    }
}

internal sealed class ProductStockMovementConfiguration : IEntityTypeConfiguration<ProductStockMovement>
{
    public void Configure(EntityTypeBuilder<ProductStockMovement> builder)
    {
        builder.Property(x => x.Sequence).UseIdentityColumn().ValueGeneratedOnAdd();
        builder.HasIndex(x => x.Sequence).IsUnique();
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.LotCode).HasMaxLength(Lengths.LotCode);
        builder.Property(x => x.DocumentCode).HasMaxLength(Lengths.LotCode);
        builder.Property(x => x.QuantityKg).HasPrecision(18, 3);
        builder.Property(x => x.TransactionPrice).HasPrecision(18, 0);
        builder.Property(x => x.PartyName).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PartyCode).HasMaxLength(Lengths.Code);
        builder.ReferencesAccount(nameof(ProductStockMovement.RecordedByAccountId));
        // The kg on hand of a product, its lots and the movements of a document are read by these.
        builder.HasIndex(x => new { x.ProductId, x.OccurredAt });
        builder.HasIndex(x => x.DocumentId);
        builder.HasIndex(x => x.HarvestLotId);
        // Undoing the rows of a document looks for the rows that reverse them.
        builder.HasIndex(x => x.ReversesMovementId);
    }
}

internal sealed class HarvestLotProductLineConfiguration : IEntityTypeConfiguration<HarvestLotProductLine>
{
    public void Configure(EntityTypeBuilder<HarvestLotProductLine> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.ProductLines).HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Kg).HasPrecision(18, 3);
        builder.HasIndex(x => new { x.LotId, x.LineNumber }).IsUnique();
        builder.HasIndex(x => x.ProductId);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_HarvestLotProductLine_CageCount", "[CageCount] > 0");
            t.HasCheckConstraint("CK_HarvestLotProductLine_Kg", "[Kg] > 0");
        });
    }
}

internal sealed class ProductDisposalConfiguration : IEntityTypeConfiguration<ProductDisposal>
{
    public void Configure(EntityTypeBuilder<ProductDisposal> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => x.DisposedAt);
        builder.HasIndex(x => x.ZoneId);
    }
}

internal sealed class ProductDisposalLineConfiguration : IEntityTypeConfiguration<ProductDisposalLine>
{
    public void Configure(EntityTypeBuilder<ProductDisposalLine> builder)
    {
        builder.HasOne(x => x.Disposal).WithMany(d => d.Lines).HasForeignKey(x => x.DisposalId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.DisposalId, x.LineNumber }).IsUnique();
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.HarvestLot).WithMany().HasForeignKey(x => x.HarvestLotId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Kg).HasPrecision(18, 3);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Url);
        builder.HasIndex(x => x.HarvestLotId);
        builder.HasIndex(x => x.ProductId);
    }
}
