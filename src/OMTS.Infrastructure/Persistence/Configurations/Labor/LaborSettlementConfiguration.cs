using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class LaborSettlementConfiguration : IEntityTypeConfiguration<LaborSettlement>
{
    public void Configure(EntityTypeBuilder<LaborSettlement> builder)
    {
        builder.HasOne(x => x.Payroll).WithMany(p => p.Settlements).HasForeignKey(x => x.PayrollId);
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.ReferencesAccount(nameof(LaborSettlement.PreparedByAccountId), nameof(LaborSettlement.ApprovedByAccountId));
        builder.HasThousandVndCheck(nameof(LaborSettlement.TotalObligation));

        // One payslip per payee and payroll.
        builder.HasIndex(x => new { x.PayrollId, x.EmployeeId }).IsUnique().HasFilter("[EmployeeId] IS NOT NULL");
        builder.HasIndex(x => new { x.PayrollId, x.WorkerId }).IsUnique().HasFilter("[WorkerId] IS NOT NULL");
        builder.HasIndex(x => new { x.PayrollId, x.ContractorId }).IsUnique().HasFilter("[ContractorId] IS NOT NULL");
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_LaborSettlement_OnePayee",
            ConfigurationHelpers.ExactlyOneNotNull(
                nameof(LaborSettlement.EmployeeId), nameof(LaborSettlement.WorkerId), nameof(LaborSettlement.ContractorId))));
    }
}
