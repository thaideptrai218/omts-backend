using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeJobTypeConfiguration : IEntityTypeConfiguration<EmployeeJobType>
{
    public void Configure(EntityTypeBuilder<EmployeeJobType> builder)
    {
        builder.HasKey(x => new { x.EmployeeId, x.JobTypeId });
        builder.HasOne(x => x.Employee).WithMany(e => e.JobTypes).HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.JobType).WithMany().HasForeignKey(x => x.JobTypeId);
    }
}
