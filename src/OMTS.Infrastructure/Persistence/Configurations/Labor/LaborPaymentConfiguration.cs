using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class LaborPaymentConfiguration : IEntityTypeConfiguration<LaborPayment>
{
    public void Configure(EntityTypeBuilder<LaborPayment> builder)
    {
        builder.HasOne(x => x.Settlement).WithMany(s => s.Payments).HasForeignKey(x => x.SettlementId);
        builder.HasOne(x => x.DeductedInSettlement).WithMany().HasForeignKey(x => x.DeductedInSettlementId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.OriginalPayment).WithMany().HasForeignKey(x => x.OriginalPaymentId);
        builder.Property(x => x.Recipient).HasMaxLength(Lengths.Name);
        builder.Property(x => x.ReferenceCode).HasMaxLength(Lengths.Code);
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Address);
        builder.Property(x => x.AdjustmentReason).HasMaxLength(Lengths.Reason);
        builder.HasThousandVndCheck(nameof(LaborPayment.AmountPaid));

        // A payslip payment, or an advance to exactly one person without a payslip (LAB-09).
        var onePerson = ConfigurationHelpers.ExactlyOneNotNull(nameof(LaborPayment.EmployeeId), nameof(LaborPayment.WorkerId));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_LaborPayment_Target",
            $"([IsAdvance] = 0 AND [SettlementId] IS NOT NULL) OR ([IsAdvance] = 1 AND [SettlementId] IS NULL AND {onePerson})"));
    }
}
