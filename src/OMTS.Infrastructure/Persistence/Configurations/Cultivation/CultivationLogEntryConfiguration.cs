using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class CultivationLogEntryConfiguration : IEntityTypeConfiguration<CultivationLogEntry>
{
    public void Configure(EntityTypeBuilder<CultivationLogEntry> builder)
    {
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Cycle).WithMany(c => c.LogEntries).HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Lane).WithMany().HasForeignKey(x => x.LaneId);
        builder.ReferencesAccount(nameof(CultivationLogEntry.RecordedByAccountId));
        builder.Property(x => x.LanePosition).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.EventType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Observation).HasMaxLength(Lengths.LongText);
        builder.Property(x => x.Observer).HasMaxLength(Lengths.Name);
        builder.HasIndex(x => new { x.ZoneId, x.OccurredAt });
    }
}
