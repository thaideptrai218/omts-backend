using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Cultivation;

namespace OMTS.Infrastructure.Persistence.Configurations.Cultivation;

internal sealed class CultivationLogConfiguration : IEntityTypeConfiguration<CultivationLog>
{
    public void Configure(EntityTypeBuilder<CultivationLog> builder)
    {
        builder.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
        builder.ReferencesAccount(nameof(CultivationLog.RecordedByAccountId));
        builder.Property(x => x.Notes).HasMaxLength(Lengths.LongText);
        builder.HasIndex(x => new { x.CycleId, x.VisitDate });
    }
}
