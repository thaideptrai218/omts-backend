using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeSalarySetupConfiguration : IEntityTypeConfiguration<EmployeeSalarySetup>
{
    public void Configure(EntityTypeBuilder<EmployeeSalarySetup> builder)
    {
        builder.Property(x => x.MonthlyAmount).HasPrecision(18, 0);
        builder.HasOne(x => x.Employee).WithMany(e => e.SalarySetups).HasForeignKey(x => x.EmployeeId);
        builder.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom }).IsUnique();
        builder.ReferencesAccount(nameof(EmployeeSalarySetup.EnteredByAccountId));
        // A fixed salary has its amount; the other types have none, and only a fixed salary may pay field shifts.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_EmployeeSalarySetups_MonthlyAmount",
                "([Type] = 'FixedMonthly' AND [MonthlyAmount] > 0) OR ([Type] IN ('Hourly', 'PerShift') AND [MonthlyAmount] IS NULL)");
            t.HasCheckConstraint("CK_EmployeeSalarySetups_PayFieldShifts", "[Type] = 'FixedMonthly' OR [PayFieldShifts] = 0");
        });
    }
}
