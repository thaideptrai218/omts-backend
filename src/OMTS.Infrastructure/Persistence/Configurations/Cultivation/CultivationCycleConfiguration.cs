using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class CultivationCycleConfiguration : IEntityTypeConfiguration<CultivationCycle>
{
    public void Configure(EntityTypeBuilder<CultivationCycle> builder)
    {
        builder.HasOne(x => x.Raft).WithMany(r => r.CultivationCycles).HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(x => x.ExpectedSeedQuantity).HasPrecision(18, 3);
        builder.Property(x => x.TargetYieldKg).HasPrecision(18, 2);
        builder.Property(x => x.Assessment).HasMaxLength(Lengths.LongText);

        // A raft holds at most one Open cycle, even for parallel starts (BR-CYCLE-01).
        builder.HasIndex(x => x.RaftId)
            .IsUnique()
            .HasFilter($"[StatusId] = {StatusIds.CultivationCycle.Open}")
            .HasDatabaseName("UX_CultivationCycles_RaftId_Open");

        builder.HasIndex(x => new { x.RaftId, x.StartDate });
    }
}
