using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class LaneSegmentConfiguration : IEntityTypeConfiguration<LaneSegment>
{
    public void Configure(EntityTypeBuilder<LaneSegment> builder)
    {
        builder.HasOne(x => x.Cycle).WithMany(c => c.LaneSegments).HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Lane).WithMany(l => l.Segments).HasForeignKey(x => x.LaneId);
        builder.HasOne(x => x.Seeding).WithMany(s => s.SeededSegments).HasForeignKey(x => x.SeedingId);
        builder.HasOne(x => x.Log).WithMany().HasForeignKey(x => x.LogId);
        builder.ReferencesAccount(nameof(LaneSegment.RecordedByAccountId), nameof(LaneSegment.ConfirmedByAccountId));

        builder.Property(x => x.FromPosition).HasPrecision(10, 2);
        builder.Property(x => x.ToPosition).HasPrecision(10, 2);
        builder.Property(x => x.PlannedKg).HasPrecision(18, 3);
        builder.Property(x => x.WriteOffReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.WriteOffPhotoUrl).HasMaxLength(Lengths.Url);
        builder.Property(x => x.PlanDeviationReason).HasMaxLength(Lengths.Reason);

        builder.HasIndex(x => new { x.CycleId, x.LaneId, x.SegmentType });
        builder.ToTable(t => t.HasCheckConstraint("CK_LaneSegment_Positions", "[ToPosition] > [FromPosition] AND [FromPosition] >= 0"));
    }
}
