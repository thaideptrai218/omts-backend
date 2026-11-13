using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Purchasing;

namespace OMTS.Infrastructure.Persistence.Configurations.Purchasing;

internal sealed class FarmingInputItemConfiguration : IEntityTypeConfiguration<FarmingInputItem>
{
    public void Configure(EntityTypeBuilder<FarmingInputItem> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        // BR-PURCHASE-10: no two items have the same name, discontinued ones included.
        builder.HasIndex(x => x.Name).IsUnique();
        builder.Property(x => x.Unit).HasMaxLength(30);
        builder.Property(x => x.Description).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.Notes).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.IllustrationUrl).HasMaxLength(Lengths.Url);
        builder.Property(x => x.MinQuantity).HasPrecision(18, 3);
        builder.Property(x => x.MaxQuantity).HasPrecision(18, 3);
        builder.Property(x => x.CostPrice).HasPrecision(18, 4);
        builder.Property(x => x.LastPurchasePrice).HasPrecision(18, 4);
        builder.HasOne(x => x.ItemGroup).WithMany().HasForeignKey(x => x.ItemGroupId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ItemGroupId);
        builder.HasIndex(x => x.IsHidden);
    }
}

internal sealed class InputUnitConfiguration : IEntityTypeConfiguration<InputUnit>
{
    public void Configure(EntityTypeBuilder<InputUnit> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(30);
        // SQL Server compares case-insensitively: "Kg" and "kg" are one unit.
        builder.HasIndex(x => x.Name).IsUnique();
    }
}

internal sealed class InputItemUnitConfiguration : IEntityTypeConfiguration<InputItemUnit>
{
    public void Configure(EntityTypeBuilder<InputItemUnit> builder)
    {
        builder.HasOne(x => x.Item).WithMany(i => i.Units).HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.Factor).HasPrecision(18, 3);
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => new { x.ItemId, x.UnitId }).IsUnique();
        // One base unit per item.
        builder.HasIndex(x => x.ItemId).IsUnique().HasFilter("[IsBase] = 1").HasDatabaseName("IX_InputItemUnits_ItemId_Base");
    }
}

internal sealed class InputItemCategoryConfiguration : IEntityTypeConfiguration<InputItemCategory>
{
    public void Configure(EntityTypeBuilder<InputItemCategory> builder)
    {
        builder.ToTable("InputItemGroups");
        builder.Property(x => x.Name).HasMaxLength(100);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ParentId);
    }
}

internal sealed class InputItemPhotoConfiguration : IEntityTypeConfiguration<InputItemPhoto>
{
    public void Configure(EntityTypeBuilder<InputItemPhoto> builder)
    {
        builder.HasOne(x => x.Item).WithMany(i => i.Photos).HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(x => x.StorageKey).HasMaxLength(Lengths.Url);
    }
}

internal sealed class InputStockMovementConfiguration : IEntityTypeConfiguration<InputStockMovement>
{
    public void Configure(EntityTypeBuilder<InputStockMovement> builder)
    {
        builder.Property(x => x.Sequence).UseIdentityColumn().ValueGeneratedOnAdd();
        builder.HasIndex(x => x.Sequence).IsUnique();
        builder.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.DocumentCode).HasMaxLength(Lengths.Code);
        builder.Property(x => x.Quantity).HasPrecision(18, 5);
        builder.Property(x => x.UnitCost).HasPrecision(18, 4);
        builder.Property(x => x.TransactionPrice).HasPrecision(18, 4);
        builder.Property(x => x.PartyName).HasMaxLength(Lengths.Name);
        builder.ReferencesAccount(nameof(InputStockMovement.RecordedByAccountId));
        // The quantity on hand of an item and the movements of a document are read by these.
        builder.HasIndex(x => new { x.ItemId, x.OccurredAt });
        builder.HasIndex(x => x.DocumentId);
        builder.HasIndex(x => x.SeedLotLineId);
    }
}
