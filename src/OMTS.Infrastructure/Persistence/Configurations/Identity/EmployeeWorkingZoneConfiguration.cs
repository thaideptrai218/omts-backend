using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Identity;

namespace OMTS.Infrastructure.Persistence.Configurations.Identity;

internal sealed class EmployeeWorkingZoneConfiguration : IEntityTypeConfiguration<EmployeeWorkingZone>
{
    public void Configure(EntityTypeBuilder<EmployeeWorkingZone> builder)
    {
        builder.HasKey(x => new { x.EmployeeId, x.ZoneId });
        builder.HasOne(x => x.Employee).WithMany(e => e.WorkingZones).HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
    }
}
