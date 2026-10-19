using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class LaborPaymentConfiguration : IEntityTypeConfiguration<LaborPayment>
{
    public void Configure(EntityTypeBuilder<LaborPayment> builder)
    {
        builder.HasOne(x => x.Settlement).WithMany(s => s.Payments).HasForeignKey(x => x.SettlementId);
        builder.HasOne(x => x.OriginalPayment).WithMany().HasForeignKey(x => x.OriginalPaymentId);
        builder.Property(x => x.Recipient).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ReferenceCode).HasMaxLength(Lengths.ShortText);
        builder.Property(x => x.AdjustmentReason).HasMaxLength(Lengths.Reason);
        builder.HasThousandVndCheck(nameof(LaborPayment.AmountPaid));
    }
}
