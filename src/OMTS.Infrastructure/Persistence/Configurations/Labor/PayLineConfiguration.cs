using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class PayLineConfiguration : IEntityTypeConfiguration<PayLine>
{
    public void Configure(EntityTypeBuilder<PayLine> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.Attendance).WithMany().HasForeignKey(x => x.AttendanceId);
        builder.HasOne(x => x.Assignment).WithMany().HasForeignKey(x => x.AssignmentId);
        builder.HasOne(x => x.PayRuleVersion).WithMany().HasForeignKey(x => x.PayRuleVersionId);
        builder.HasOne(x => x.Settlement).WithMany(s => s.PayLines).HasForeignKey(x => x.SettlementId);
        builder.Property(x => x.Description).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.HasThousandVndCheck(nameof(PayLine.Amount));
        builder.ToTable(t => t.HasCheckConstraint("CK_PayLine_Amount_NotNegative", "[Amount] >= 0"));
    }
}
