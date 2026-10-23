using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Traceability;

namespace OMTS.Infrastructure.Persistence.Configurations.Traceability;

internal sealed class TraceabilityLotConfiguration : IEntityTypeConfiguration<TraceabilityLot>
{
    public void Configure(EntityTypeBuilder<TraceabilityLot> builder)
    {
        builder.Property(x => x.LotCode).HasMaxLength(Lengths.Code);
        builder.HasIndex(x => x.LotCode).IsUnique();
        builder.Property(x => x.QrCode).HasMaxLength(Lengths.ShortText);
        builder.HasIndex(x => x.QrCode).IsUnique();

        // One first-sale order creates at most one lot (BR-09).
        builder.HasOne(x => x.InitialOrder).WithMany().HasForeignKey(x => x.InitialOrderId);
        builder.HasIndex(x => x.InitialOrderId).IsUnique();
        builder.ReferencesAccount(nameof(TraceabilityLot.DisposedByAccountId));

        builder.Property(x => x.DisposalMethod).HasMaxLength(Lengths.Name);
        builder.Property(x => x.DisposalReason).HasMaxLength(Lengths.Reason);
    }
}
