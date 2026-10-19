using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class LaborSettlementConfiguration : IEntityTypeConfiguration<LaborSettlement>
{
    public void Configure(EntityTypeBuilder<LaborSettlement> builder)
    {
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.ReferencesAccount(nameof(LaborSettlement.PreparedByAccountId), nameof(LaborSettlement.ApprovedByAccountId));
        builder.HasThousandVndCheck(nameof(LaborSettlement.TotalObligation));
    }
}
