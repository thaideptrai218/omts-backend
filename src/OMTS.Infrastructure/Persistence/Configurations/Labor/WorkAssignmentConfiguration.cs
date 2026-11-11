using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class WorkAssignmentConfiguration : IEntityTypeConfiguration<WorkAssignment>
{
    public void Configure(EntityTypeBuilder<WorkAssignment> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.JobType).WithMany().HasForeignKey(x => x.JobTypeId);
        builder.ReferencesAccount(nameof(WorkAssignment.AssignedByAccountId));
        builder.Property(x => x.CorrectionReason).HasMaxLength(Lengths.Reason);

        // Before the check-in an employee has at most one assignment per date (BR-LABOR-03): the first job period of the day, which has
        // no start time. Later job periods of the day have one, so they are outside this index; parallel saves cannot both add a first one.
        builder.HasIndex(x => new { x.EmployeeId, x.Date })
            .IsUnique()
            .HasDatabaseName("UX_WorkAssignments_Employee_Date_First")
            .HasFilter("[StartsAt] IS NULL");
        builder.HasIndex(x => new { x.Date, x.RaftId });
        builder.HasIndex(x => x.RaftId);
        builder.HasIndex(x => x.JobTypeId);
        builder.HasIndex(x => x.AssignedByAccountId);
    }
}
