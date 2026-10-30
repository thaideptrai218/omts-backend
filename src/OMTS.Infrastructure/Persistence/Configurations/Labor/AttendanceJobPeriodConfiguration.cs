using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class AttendanceJobPeriodConfiguration : IEntityTypeConfiguration<AttendanceJobPeriod>
{
    public void Configure(EntityTypeBuilder<AttendanceJobPeriod> builder)
    {
        builder.HasOne(x => x.Attendance).WithMany(a => a.JobPeriods).HasForeignKey(x => x.AttendanceId);
        builder.HasOne(x => x.JobType).WithMany().HasForeignKey(x => x.JobTypeId);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasIndex(x => new { x.AttendanceId, x.StartsAt }).IsUnique();
    }
}
