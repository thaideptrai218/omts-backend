using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Harvest;

namespace OMTS.Infrastructure.Persistence.Configurations.Harvest;

internal sealed class HarvestLotConfiguration : IEntityTypeConfiguration<HarvestLot>
{
    // A zone code, a raft code, the date and a suffix (BR-HARVEST-03).
    private const int LotCodeLength = 120;

    public void Configure(EntityTypeBuilder<HarvestLot> builder)
    {
        builder.Property(x => x.LotCode).HasMaxLength(LotCodeLength);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(nameof(HarvestLot.RecordedByAccountId));
        builder.Property(x => x.KgPerCage).HasPrecision(9, 2);
        builder.Property(x => x.EstimatedKg).HasPrecision(18, 3);
        builder.Property(x => x.LeftoverKg).HasPrecision(18, 3);
        builder.Property(x => x.WithdrawalReason).HasMaxLength(Lengths.Reason);

        // The unique index holds the lot code when two saves read the same last code; the loser takes the next one.
        builder.HasIndex(x => x.LotCode).IsUnique();
        builder.HasIndex(x => new { x.RaftId, x.HarvestDate });
        builder.HasIndex(x => x.CycleId);
        builder.HasIndex(x => new { x.StatusId, x.HarvestDate });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_HarvestLot_CageCount", "[CageCount] > 0");
            t.HasCheckConstraint("CK_HarvestLot_KgPerCage", "[KgPerCage] > 0");
            t.HasCheckConstraint("CK_HarvestLot_EstimatedKg", "[EstimatedKg] > 0");
            t.HasCheckConstraint("CK_HarvestLot_LeftoverKg", "[LeftoverKg] IS NULL OR [LeftoverKg] >= 0");
        });
    }
}

internal sealed class HarvestLotLaneConfiguration : IEntityTypeConfiguration<HarvestLotLane>
{
    public void Configure(EntityTypeBuilder<HarvestLotLane> builder)
    {
        builder.HasKey(x => new { x.LotId, x.LaneId });
        builder.HasOne(x => x.Lot).WithMany(l => l.Lanes).HasForeignKey(x => x.LotId);
        builder.HasOne(x => x.Lane).WithMany().HasForeignKey(x => x.LaneId);
    }
}

internal sealed class HarvestLotPhotoConfiguration : IEntityTypeConfiguration<HarvestLotPhoto>
{
    public void Configure(EntityTypeBuilder<HarvestLotPhoto> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.Photos).HasForeignKey(x => x.LotId);
        builder.Property(x => x.FileKey).HasMaxLength(Lengths.Url);

        // One file per slot and only the allowed slots: no save can leave more than the maximum on a lot.
        builder.HasIndex(x => new { x.LotId, x.SortOrder }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_HarvestLotPhoto_SortOrder", $"[SortOrder] BETWEEN 1 AND {HarvestLot.MaxPhotos}"));
    }
}
