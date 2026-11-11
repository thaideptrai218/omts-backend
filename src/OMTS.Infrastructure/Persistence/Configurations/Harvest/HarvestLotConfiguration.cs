using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Harvest;

internal sealed class HarvestLotConfiguration : IEntityTypeConfiguration<HarvestLot>
{
    public void Configure(EntityTypeBuilder<HarvestLot> builder)
    {
        builder.Property(x => x.LotCode).HasMaxLength(Lengths.LotCode);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(nameof(HarvestLot.RecordedByAccountId));
        // The purchase request the trip harvested for (decision of 11/11/2026); the lots of a request are listed by it.
        builder.HasOne(x => x.PurchaseRequest).WithMany().HasForeignKey(x => x.PurchaseRequestId);
        // The invoice whose "sold the whole lot" tick closed the lot (SALE-05); only that invoice reopens it (SALE-07, SALE-08).
        builder.HasOne<SalesInvoice>().WithMany().HasForeignKey(x => x.ClosedBySalesInvoiceId);
        builder.Property(x => x.CageCount).HasPrecision(9, 2);
        builder.Property(x => x.OverPlanReason).HasMaxLength(Lengths.Reason);
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

internal sealed class HarvestLotCrewMemberConfiguration : IEntityTypeConfiguration<HarvestLotCrewMember>
{
    public void Configure(EntityTypeBuilder<HarvestLotCrewMember> builder)
    {
        // One row per employee on the lot; the same employee may be on lots of other rafts the same day (decision of 12/11/2026).
        builder.HasKey(x => new { x.LotId, x.EmployeeId });
        builder.HasOne(x => x.Lot).WithMany(l => l.Crew).HasForeignKey(x => x.LotId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.EmployeeId);
    }
}
