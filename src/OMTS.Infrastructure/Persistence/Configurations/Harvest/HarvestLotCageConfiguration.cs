using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Infrastructure.Persistence.Configurations.Harvest;

internal sealed class HarvestLotCageConfiguration : IEntityTypeConfiguration<HarvestLotCage>
{
    public void Configure(EntityTypeBuilder<HarvestLotCage> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.AllCages).HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(HarvestLotCage.WeighedByAccountId));
        builder.Property(x => x.Kg).HasPrecision(18, 3);

        // One number per cage of a lot and side: two cages weighed at once on one side cannot take the same number, the loser takes the next one.
        builder.HasIndex(x => new { x.LotId, x.Side, x.Number }).IsUnique();
        builder.HasIndex(x => x.ProductId);
        // A cage sent again after a lost answer is found by its operation id; the two sides keep their own ids.
        builder.HasIndex(x => new { x.Side, x.ClientOperationId }).IsUnique().HasFilter("[ClientOperationId] IS NOT NULL");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_HarvestLotCage_Number", "[Number] >= 1");
            t.HasCheckConstraint("CK_HarvestLotCage_Kg", "[Kg] > 0");
            // The trader weighs the lot as a whole, not per product.
            t.HasCheckConstraint("CK_HarvestLotCage_TraderProduct", "[Side] <> 'Trader' OR [ProductId] IS NULL");
        });
    }
}
