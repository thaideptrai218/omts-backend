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

        // An employee has at most one work assignment before the check-in (BR-LABOR-03): two saves that assign the same
        // employee on one date meet this index, whichever raft they name.
        builder.HasIndex(x => new { x.EmployeeId, x.WorkDate }).IsUnique().HasFilter("[StartTime] IS NULL")
            .HasDatabaseName("UX_WorkAssignments_EmployeeId_WorkDate_FirstPeriod");

        // The job periods of an employee on a date, and the crew of a raft on a date.
        builder.HasIndex(x => new { x.EmployeeId, x.WorkDate, x.StartTime });
        builder.HasIndex(x => new { x.RaftId, x.WorkDate });
    }
}
