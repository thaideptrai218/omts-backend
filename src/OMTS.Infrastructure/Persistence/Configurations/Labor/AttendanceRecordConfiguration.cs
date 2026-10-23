using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> builder)
    {
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.ReferencesAccount(nameof(AttendanceRecord.RecordedByAccountId));
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.Hours).HasPrecision(4, 1);

        // One record per person per day across all zones (E41, LAB-04).
        builder.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique().HasFilter("[EmployeeId] IS NOT NULL");
        builder.HasIndex(x => new { x.WorkerId, x.WorkDate }).IsUnique().HasFilter("[WorkerId] IS NOT NULL");
        builder.HasIndex(x => new { x.ZoneId, x.WorkDate });
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AttendanceRecord_OnePerson",
            ConfigurationHelpers.ExactlyOneNotNull(nameof(AttendanceRecord.EmployeeId), nameof(AttendanceRecord.WorkerId))));
    }
}
