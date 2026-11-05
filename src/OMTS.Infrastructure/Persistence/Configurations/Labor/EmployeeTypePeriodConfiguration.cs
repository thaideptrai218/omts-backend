using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeTypePeriodConfiguration : IEntityTypeConfiguration<EmployeeTypePeriod>
{
    public void Configure(EntityTypeBuilder<EmployeeTypePeriod> builder)
    {
        builder.HasOne(x => x.Employee).WithMany(e => e.TypePeriods).HasForeignKey(x => x.EmployeeId);
        builder.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom }).IsUnique();
    }
}
