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
        builder.HasOne(x => x.PlannedSeedType).WithMany().HasForeignKey(x => x.PlannedSeedTypeId);
        builder.Property(x => x.SeedingCompletionReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ClosingSummary).HasMaxLength(Lengths.LongText);

        // A raft holds at most one Open cycle; a new one starts only after closing (BR-21).
        builder.HasIndex(x => x.RaftId)
            .IsUnique()
            .HasFilter($"[StatusId] = {StatusIds.CultivationCycle.Open}")
            .HasDatabaseName("UX_CultivationCycles_RaftId_Open");
    }
}
