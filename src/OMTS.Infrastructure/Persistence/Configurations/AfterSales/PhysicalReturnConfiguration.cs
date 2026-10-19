using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.AfterSales;

namespace OMTS.Infrastructure.Persistence.Configurations.AfterSales;

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
