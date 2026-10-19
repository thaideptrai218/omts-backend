using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Farming;

namespace OMTS.Infrastructure.Persistence.Configurations.Farming;

internal sealed class LaneConfiguration : IEntityTypeConfiguration<Lane>
{
    public void Configure(EntityTypeBuilder<Lane> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.Lanes).HasForeignKey(x => x.RaftId);
        builder.HasIndex(x => new { x.RaftId, x.LaneNumber }).IsUnique();
        builder.Property(x => x.Length).HasPrecision(10, 2);
        builder.Property(x => x.MeasurementMarkers).HasMaxLength(Lengths.Address);
    }
}
