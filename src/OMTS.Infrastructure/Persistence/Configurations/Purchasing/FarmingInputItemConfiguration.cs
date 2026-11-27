using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
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
        builder.Property(x => x.MinQuantity).HasPrecision(18, 3);
        builder.Property(x => x.MaxQuantity).HasPrecision(18, 3);
        builder.Property(x => x.CostPrice).HasPrecision(18, 4);
        builder.Property(x => x.LastPurchasePrice).HasPrecision(18, 4);
        builder.HasOne(x => x.ItemGroup).WithMany().HasForeignKey(x => x.ItemGroupId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ItemGroupId);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ProductId);
        builder.HasIndex(x => x.IsHidden);
        builder.HasOne(x => x.PurchaseVatRate).WithMany().HasForeignKey(x => x.PurchaseVatRateId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.PurchaseVatRateId);
    }
}

internal sealed class VatRateConfiguration : IEntityTypeConfiguration<VatRate>
{
    /// <summary>The seeded rates carry this creation time; they are made by the system, not by a person.</summary>
    private static readonly DateTimeOffset SeededAt = new(2026, 11, 28, 0, 0, 0, TimeSpan.FromHours(7));

    public void Configure(EntityTypeBuilder<VatRate> builder)
    {
        builder.Property(x => x.Name).HasMaxLength(50);
        builder.Property(x => x.Percent).HasPrecision(5, 2);
        // SQL Server compares case-insensitively: "kct" and "KCT" are one rate.
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasData(
            Seed(VatRateIds.Kct, "KCT", null, isSystem: true, isDefault: false, sortOrder: 0),
            Seed(VatRateIds.Zero, "0%", 0m, isSystem: false, isDefault: false, sortOrder: 1),
            Seed(VatRateIds.Five, "5%", 5m, isSystem: false, isDefault: false, sortOrder: 2),
            Seed(VatRateIds.Eight, "8%", 8m, isSystem: false, isDefault: false, sortOrder: 3),
            Seed(VatRateIds.Ten, "10%", 10m, isSystem: false, isDefault: true, sortOrder: 4));
    }

    // Anonymous objects: the key has no public setter.
    private static object Seed(Guid id, string name, decimal? percent, bool isSystem, bool isDefault, int sortOrder) => new
    {
        Id = id,
        Name = name,
        Percent = percent,
        IsExempt = percent is null,
        IsSystem = isSystem,
        IsDefault = isDefault,
        IsActive = true,
        SortOrder = sortOrder,
        CreatedAt = SeededAt,
        // The in-memory test provider needs a value for the row version; SQL Server generates its own and takes none.
        RowVersion = Array.Empty<byte>(),
    };
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
        builder.Property(x => x.PreviousUnitCost).HasPrecision(18, 4);
        builder.Property(x => x.TransactionPrice).HasPrecision(18, 4);
        builder.Property(x => x.PartyName).HasMaxLength(Lengths.Name);
        builder.ReferencesAccount(nameof(InputStockMovement.RecordedByAccountId));
        // The quantity on hand of an item and the movements of a document are read by these.
        // The quantity and the lot ride along so adding up the stock of an item needs no lookup into the row.
        builder.HasIndex(x => new { x.ItemId, x.OccurredAt }).IncludeProperties(x => new { x.Quantity, x.SeedLotLineId });
        builder.HasIndex(x => x.DocumentId);
        builder.HasIndex(x => x.SeedLotLineId);
        // Undoing the rows of a document looks for the rows that reverse them; most rows reverse nothing.
        builder.HasIndex(x => x.ReversesMovementId).HasFilter("[ReversesMovementId] IS NOT NULL");
    }
}
