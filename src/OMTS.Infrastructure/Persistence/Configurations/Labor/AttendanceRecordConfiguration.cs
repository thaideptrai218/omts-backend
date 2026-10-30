using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class AttendanceRecordConfiguration : IEntityTypeConfiguration<AttendanceRecord>
{
    public void Configure(EntityTypeBuilder<AttendanceRecord> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.ReferencesAccount(nameof(AttendanceRecord.RecordedByAccountId));
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Reason);
        builder.HasOne<OMTS.Domain.Entities.Farming.FarmingZone>().WithMany().HasForeignKey(x => x.ZoneId);
        builder.Property(x => x.CheckInMethod).HasMaxLength(30);
        builder.Property(x => x.CheckOutMethod).HasMaxLength(30);
        builder.ToTable(t => t.HasCheckConstraint("CK_AttendanceRecord_Times",
            "[CheckedInAt] IS NULL OR [CheckedOutAt] IS NULL OR [CheckedOutAt] > [CheckedInAt]"));

        // One record per person per day (E41).
        builder.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique().HasFilter("[EmployeeId] IS NOT NULL");
        builder.HasIndex(x => new { x.WorkerId, x.WorkDate }).IsUnique().HasFilter("[WorkerId] IS NOT NULL");
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_AttendanceRecord_OnePerson",
            ConfigurationHelpers.ExactlyOneNotNull(nameof(AttendanceRecord.EmployeeId), nameof(AttendanceRecord.WorkerId))));
    }
}
