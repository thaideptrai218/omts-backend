using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class AttendanceConfiguration : IEntityTypeConfiguration<Attendance>
{
    public void Configure(EntityTypeBuilder<Attendance> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.ReferencesAccount(nameof(Attendance.CheckInRecordedByAccountId), nameof(Attendance.CheckOutRecordedByAccountId));
        builder.Property(x => x.CheckInReason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CheckOutReason).HasMaxLength(Lengths.Reason);
        builder.Ignore(x => x.IsComplete);

        // An employee has at most one attendance record per day (BR-LABOR-06); parallel recordings cannot both add it.
        builder.HasIndex(x => new { x.EmployeeId, x.Date }).IsUnique();
        builder.HasIndex(x => x.Date);
        builder.HasIndex(x => x.CheckInRecordedByAccountId);
        builder.HasIndex(x => x.CheckOutRecordedByAccountId);
    }
}
