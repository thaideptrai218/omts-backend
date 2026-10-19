using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.AfterSales;

namespace OMTS.Infrastructure.Persistence.Configurations.AfterSales;

internal sealed class QualityClaimConfiguration : IEntityTypeConfiguration<QualityClaim>
{
    public void Configure(EntityTypeBuilder<QualityClaim> builder)
    {
        builder.HasOne(x => x.Sale).WithMany(s => s.Claims).HasForeignKey(x => x.SaleId);
        builder.ReferencesAccount(nameof(QualityClaim.RecordedByAccountId), nameof(QualityClaim.DecidedByAccountId));
        builder.Property(x => x.AffectedPortion).HasMaxLength(Lengths.Address);
        builder.Property(x => x.ComplaintType).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.Description).HasMaxLength(Lengths.LongText);
    }
}
