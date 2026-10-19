using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class WorkAssignmentConfiguration : IEntityTypeConfiguration<WorkAssignment>
{
    public void Configure(EntityTypeBuilder<WorkAssignment> builder)
    {
        builder.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId);
        builder.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        builder.HasOne(x => x.Cycle).WithMany().HasForeignKey(x => x.CycleId);
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.HasOne(x => x.Worker).WithMany().HasForeignKey(x => x.WorkerId);
        builder.HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId);
        builder.ReferencesAccount(nameof(WorkAssignment.CancelledByAccountId));

        builder.Property(x => x.Task).HasMaxLength(Lengths.Address);
        builder.Property(x => x.CancellationReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.ZoneId, x.WorkDate });

        // Daily wage: exactly one employee or seasonal worker. Task contract: a contractor and an amount (E40).
        var oneWorker = ConfigurationHelpers.ExactlyOneNotNull(nameof(WorkAssignment.EmployeeId), nameof(WorkAssignment.WorkerId));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_WorkAssignment_PaymentType",
            $"([PaymentType] = N'DailyWage' AND {oneWorker}) OR " +
            "([PaymentType] = N'TaskContract' AND [ContractorId] IS NOT NULL AND [ContractAmount] IS NOT NULL)"));
    }
}
