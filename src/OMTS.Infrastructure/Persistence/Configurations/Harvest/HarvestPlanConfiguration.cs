using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Harvest;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Harvest;

internal sealed class HarvestPlanConfiguration : IEntityTypeConfiguration<HarvestPlan>
{
    public void Configure(EntityTypeBuilder<HarvestPlan> builder)
    {
        builder.Property(x => x.Code).HasMaxLength(Lengths.Code);
        // Unique regardless of letter case: the column uses the case-insensitive collation of the database.
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        builder.ReferencesAccount(
            nameof(HarvestPlan.CreatedByAccountId), nameof(HarvestPlan.AssignedByAccountId), nameof(HarvestPlan.CompletedByAccountId), nameof(HarvestPlan.CancelledByAccountId));
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);

        // The list: by status and planned day.
        builder.HasIndex(x => new { x.StatusId, x.PlannedDate });
    }
}

internal sealed class HarvestPlanLineConfiguration : IEntityTypeConfiguration<HarvestPlanLine>
{
    public void Configure(EntityTypeBuilder<HarvestPlanLine> builder)
    {
        builder.HasOne(x => x.Plan).WithMany(p => p.Lines).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.PlanId, x.LineNumber });
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.RaftId);
        builder.HasOne(x => x.ExpectedProduct).WithMany().HasForeignKey(x => x.ExpectedProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.ExpectedCageCount).HasPrecision(9, 2);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Url);
        builder.ToTable(t => t.HasCheckConstraint("CK_HarvestPlanLine_ExpectedCageCount", "[ExpectedCageCount] IS NULL OR [ExpectedCageCount] > 0"));

        // One line makes at most one lot: the lot holds the link, and the unique index on it (filtered by EF, as the link is optional) refuses a second lot on the line.
        builder.HasOne(x => x.Lot).WithOne(l => l.HarvestPlanLine).HasForeignKey<HarvestLot>(l => l.HarvestPlanLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalesOrderHarvestPlanConfiguration : IEntityTypeConfiguration<SalesOrderHarvestPlan>
{
    public void Configure(EntityTypeBuilder<SalesOrderHarvestPlan> builder)
    {
        builder.HasKey(x => new { x.OrderId, x.PlanId });
        builder.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Plan).WithMany(p => p.Orders).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.PlanId);
    }
}
