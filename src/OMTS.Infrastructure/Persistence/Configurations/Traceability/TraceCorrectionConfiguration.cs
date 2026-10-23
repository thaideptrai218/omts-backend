using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Traceability;

namespace OMTS.Infrastructure.Persistence.Configurations.Traceability;

internal sealed class TraceCorrectionConfiguration : IEntityTypeConfiguration<TraceCorrection>
{
    public void Configure(EntityTypeBuilder<TraceCorrection> builder)
    {
        builder.HasOne(x => x.Lot).WithMany(l => l.Corrections).HasForeignKey(x => x.LotId);
        builder.ReferencesAccount(nameof(TraceCorrection.CorrectedByAccountId), nameof(TraceCorrection.ApprovedByAccountId));
        builder.Property(x => x.Field).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.PublicationImpact).HasMaxLength(Lengths.Address);
    }
}
