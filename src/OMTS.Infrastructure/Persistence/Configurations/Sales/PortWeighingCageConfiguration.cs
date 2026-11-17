using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PortWeighingCageConfiguration : IEntityTypeConfiguration<PortWeighingCage>
{
    public void Configure(EntityTypeBuilder<PortWeighingCage> builder)
    {
        builder.HasOne(x => x.PortWeighing).WithMany(w => w.Cages).HasForeignKey(x => x.PortWeighingId);
        builder.HasOne(x => x.HarvestLot).WithMany().HasForeignKey(x => x.HarvestLotId);
        builder.HasIndex(x => new { x.PortWeighingId, x.CageNumber }).IsUnique();
        builder.HasIndex(x => x.HarvestLotId);
        builder.Property(x => x.Kg).HasPrecision(18, 3);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PortWeighingCage_Kg", "[Kg] > 0");
            t.HasCheckConstraint("CK_PortWeighingCage_Number", "[CageNumber] >= 1");
        });
    }
}
