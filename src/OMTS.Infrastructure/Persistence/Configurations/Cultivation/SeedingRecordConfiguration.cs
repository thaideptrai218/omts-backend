using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;
using OMTS.Domain.Entities.Farming;

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
        // The share of the lane this session hung: 100 for the rows saved before the percentages.
        builder.Property(x => x.SeededPercent).HasPrecision(5, 2).HasDefaultValue(100m);
        builder.ToTable(t => t.HasCheckConstraint("CK_SeedingRecordLane_SeededPercent", "[SeededPercent] > 0 AND [SeededPercent] <= 100"));
    }
}

internal sealed class SeedingRecordPhotoConfiguration : IEntityTypeConfiguration<SeedingRecordPhoto>
{
    public void Configure(EntityTypeBuilder<SeedingRecordPhoto> builder)
    {
        builder.HasOne(x => x.SeedingRecord).WithMany(r => r.Photos).HasForeignKey(x => x.SeedingRecordId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);

        // One file per slot and only the allowed slots: no save can leave more than the maximum on a session.
        builder.HasIndex(x => new { x.SeedingRecordId, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_SeedingRecordPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {SeedingRecord.MaxPhotos}"));
    }
}

internal sealed class CyclePlanPhotoConfiguration : IEntityTypeConfiguration<CyclePlanPhoto>
{
    public void Configure(EntityTypeBuilder<CyclePlanPhoto> builder)
    {
        builder.HasOne(x => x.Cycle).WithMany(c => c.PlanPhotos).HasForeignKey(x => x.CycleId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);
        builder.HasIndex(x => new { x.CycleId, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_CyclePlanPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {CultivationCycle.MaxPlanPhotos}"));
    }
}

internal sealed class LaneProgressChangeConfiguration : IEntityTypeConfiguration<LaneProgressChange>
{
    public void Configure(EntityTypeBuilder<LaneProgressChange> builder)
    {
        builder.HasOne<CultivationCycle>().WithMany().HasForeignKey(x => x.CycleId);
        builder.HasOne<Lane>().WithMany().HasForeignKey(x => x.LaneId);
        builder.ReferencesAccount(nameof(LaneProgressChange.ChangedByAccountId));
        builder.Property(x => x.Reason).HasMaxLength(LaneProgressChange.MaxReasonLength);
        builder.Property(x => x.OldSeededPercent).HasPrecision(5, 2);
        builder.Property(x => x.OldHarvestedPercent).HasPrecision(5, 2);
        builder.Property(x => x.SeededPercent).HasPrecision(5, 2);
        builder.Property(x => x.HarvestedPercent).HasPrecision(5, 2);
        builder.HasIndex(x => new { x.CycleId, x.LaneId, x.OccurredAt });
    }
}
