using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Traceability;

namespace OMTS.Infrastructure.Persistence.Configurations.Traceability;

internal sealed class HarvestEventConfiguration : IEntityTypeConfiguration<HarvestEvent>
{
    public void Configure(EntityTypeBuilder<HarvestEvent> builder)
    {
        builder.HasOne(x => x.Order).WithMany(o => o.HarvestEvents).HasForeignKey(x => x.OrderId);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.ReferencesAccount(nameof(HarvestEvent.RecordedByAccountId));
        builder.Property(x => x.EstimatedKg).HasPrecision(18, 3);
        builder.Property(x => x.LotPhotoUrl).HasMaxLength(Lengths.Url);
    }
}
