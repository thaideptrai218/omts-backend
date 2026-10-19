using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Platform;

namespace OMTS.Infrastructure.Persistence.Configurations.Platform;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasOne(x => x.ActorAccount).WithMany().HasForeignKey(x => x.ActorAccountId);
        builder.Property(x => x.Action).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.RecordCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ViewScope).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => new { x.RecordType, x.RecordId });
        builder.HasIndex(x => x.OccurredAt);
    }
}
