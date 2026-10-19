using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities;

namespace OMTS.Infrastructure.Persistence.Configurations;

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

internal sealed class PhysicalReturnConfiguration : IEntityTypeConfiguration<PhysicalReturn>
{
    public void Configure(EntityTypeBuilder<PhysicalReturn> builder)
    {
        builder.HasOne(x => x.Sale).WithMany().HasForeignKey(x => x.SaleId);
        builder.HasOne(x => x.Lot).WithMany().HasForeignKey(x => x.LotId);
        builder.ReferencesAccount(nameof(PhysicalReturn.AssessedByAccountId));

        builder.Property(x => x.ReceivedQuantityKg).HasPrecision(18, 3);
        builder.Property(x => x.ResellableQuantityKg).HasPrecision(18, 3);
        builder.Property(x => x.OtherDisposalQuantityKg).HasPrecision(18, 3);
        builder.Property(x => x.Condition).HasMaxLength(Lengths.Address);
        builder.Property(x => x.HoldingPlace).HasMaxLength(Lengths.Name);
        builder.Property(x => x.AssessmentBasis).HasMaxLength(Lengths.Reason);
    }
}
