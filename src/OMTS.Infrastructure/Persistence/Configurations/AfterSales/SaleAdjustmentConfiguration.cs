using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.AfterSales;

namespace OMTS.Infrastructure.Persistence.Configurations.AfterSales;

internal sealed class SaleAdjustmentConfiguration : IEntityTypeConfiguration<SaleAdjustment>
{
    public void Configure(EntityTypeBuilder<SaleAdjustment> builder)
    {
        builder.HasOne(x => x.Sale).WithMany(s => s.Adjustments).HasForeignKey(x => x.SaleId);
        builder.HasOne(x => x.Claim).WithMany().HasForeignKey(x => x.ClaimId);
        builder.HasOne(x => x.NewWeighing).WithMany().HasForeignKey(x => x.NewWeighingId);
        builder.ReferencesAccount(nameof(SaleAdjustment.DecidedByAccountId), nameof(SaleAdjustment.ApprovedByAccountId));

        builder.Property(x => x.Percentage).HasPrecision(5, 2);
        builder.Property(x => x.NegotiationBasis).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Decision).HasMaxLength(Lengths.Address);
        builder.HasThousandVndCheck(nameof(SaleAdjustment.Amount));
    }
}
