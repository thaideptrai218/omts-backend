using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class FarmingZoneConfiguration : IEntityTypeConfiguration<FarmingZone>
{
    public void Configure(EntityTypeBuilder<FarmingZone> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(Lengths.Name);
        builder.Property(x => x.Location).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Latitude).HasPrecision(9, 6);
        builder.Property(x => x.Longitude).HasPrecision(9, 6);
        builder.HasCoordinateChecks();
        builder.Property(x => x.Area).HasPrecision(18, 4);
        builder.Property(x => x.WaterMeasurementPoint).HasMaxLength(Lengths.Name);
        builder.Property(x => x.PhMin).HasPrecision(4, 2);
        builder.Property(x => x.PhMax).HasPrecision(4, 2);
        builder.Property(x => x.SalinityMin).HasPrecision(5, 2);
        builder.Property(x => x.SalinityMax).HasPrecision(5, 2);

        builder.HasOne(x => x.Cooperative).WithMany(c => c.FarmingZones).HasForeignKey(x => x.CooperativeId);
    }
}
