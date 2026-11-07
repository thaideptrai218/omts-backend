using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class SeedingEventConfiguration : IEntityTypeConfiguration<SeedingEvent>
{
    public void Configure(EntityTypeBuilder<SeedingEvent> builder)
    {
        builder.HasOne(x => x.Cycle).WithMany(c => c.SeedingEvents).HasForeignKey(x => x.CycleId);
        // The seed lot is a Seed line of a purchase receipt (GBR-04); a seeded line can never be removed.
        builder.HasOne(x => x.SeedLot).WithMany().HasForeignKey(x => x.SeedLotId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(SeedingEvent.RecordedByAccountId));
        builder.Property(x => x.Workers).HasMaxLength(Lengths.Address);
        builder.Property(x => x.SeedQuantity).HasPrecision(18, 3);
        builder.Property(x => x.PlanDeviationReason).HasMaxLength(Lengths.Reason);
    }
}
