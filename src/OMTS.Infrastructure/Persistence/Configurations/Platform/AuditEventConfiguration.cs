using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Application.Features.Harvest;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasOne(x => x.ActorAccount).WithMany().HasForeignKey(x => x.ActorAccountId);
        builder.Property(x => x.Action).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordCode).HasMaxLength(Lengths.LotCode);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ViewScope).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => new { x.RecordType, x.RecordId });
        builder.HasIndex(x => x.OccurredAt);
        // Audit search by zone and period (ACS-11).
        builder.HasIndex(x => new { x.ZoneId, x.OccurredAt });

        // A lot code is given once, also after its lot is removed (BR-HARVEST-05, PM default): the Created row outlives the lot and commits with it, so this index
        // refuses a save that read the codes before a lot took the code and was removed again; the loser takes the next code. It also serves the read of the codes.
        builder.HasIndex(x => x.RecordCode)
            .IsUnique()
            .HasDatabaseName("UX_AuditEvents_HarvestLotCreatedCode")
            .HasFilter($"[Action] = '{HarvestLotAudit.Created}' AND [RecordCode] IS NOT NULL");
    }
}
