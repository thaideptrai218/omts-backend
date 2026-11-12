using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class SeedingRecordConfiguration : IEntityTypeConfiguration<SeedingRecord>
{
    public void Configure(EntityTypeBuilder<SeedingRecord> builder)
    {
        builder.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
        // The stock issue this session posted: one fact, linked both ways (the issue holds the session id); null on a legacy session.
        builder.HasOne(x => x.Issue).WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
        // The seed lot is a Seed line of a purchase receipt (GBR-04); a seeded line can never be removed.
        builder.HasOne(x => x.SeedLotLine).WithMany().HasForeignKey(x => x.SeedLotLineId);
        // The nursery batch the seed came from; null on a session of a cycle started the old way.
        builder.HasOne<NurseryBatch>().WithMany().HasForeignKey(x => x.NurseryBatchId);
        builder.HasIndex(x => x.NurseryBatchId);
        builder.ReferencesAccount(nameof(SeedingRecord.RecordedByAccountId));
        builder.Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Property(x => x.OverPlanReason).HasMaxLength(1000);
        builder.HasIndex(x => new { x.CycleId, x.SeedingDate });
        builder.ToTable(t => t.HasCheckConstraint("CK_SeedingRecord_Quantity", "[Quantity] > 0"));
    }
}

internal sealed class SeedingRecordLaneConfiguration : IEntityTypeConfiguration<SeedingRecordLane>
{
    public void Configure(EntityTypeBuilder<SeedingRecordLane> builder)
    {
        builder.HasKey(x => new { x.SeedingRecordId, x.LaneId });
        builder.HasOne(x => x.SeedingRecord).WithMany(r => r.Lanes).HasForeignKey(x => x.SeedingRecordId);
        builder.HasOne(x => x.Lane).WithMany().HasForeignKey(x => x.LaneId);
    }
}
