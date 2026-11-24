using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Infrastructure.Persistence.Configurations.Harvest;

internal sealed class HarvestLotTraderCageConfiguration : IEntityTypeConfiguration<HarvestLotTraderCage>
{
    public void Configure(EntityTypeBuilder<HarvestLotTraderCage> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.TraderCages).HasForeignKey(x => x.LotId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(HarvestLotTraderCage.WeighedByAccountId));
        builder.Property(x => x.Kg).HasPrecision(18, 3);

        // One number per cage of a lot, as for the cooperative's cages: two cages weighed at once cannot take the same number.
        builder.HasIndex(x => new { x.LotId, x.Number }).IsUnique();
        builder.HasIndex(x => x.ClientOperationId).IsUnique().HasFilter("[ClientOperationId] IS NOT NULL");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_HarvestLotTraderCage_Number", "[Number] >= 1");
            t.HasCheckConstraint("CK_HarvestLotTraderCage_Kg", "[Kg] > 0");
        });
    }
}
