using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class PayRuleVersionConfiguration : IEntityTypeConfiguration<PayRuleVersion>
{
    public void Configure(EntityTypeBuilder<PayRuleVersion> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.ReferencesAccount(nameof(PayRuleVersion.EnteredByAccountId), nameof(PayRuleVersion.ApprovedByAccountId));
        builder.HasIndex(x => new { x.SubjectType, x.EmployeeId, x.ValidFrom });
    }
}
