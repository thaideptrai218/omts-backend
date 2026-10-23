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
        builder.ReferencesAccount(nameof(WorkAssignment.CancelledByAccountId), nameof(WorkAssignment.CompletionConfirmedByAccountId));

        builder.Property(x => x.Task).HasMaxLength(Lengths.Address);
        builder.Property(x => x.Notes).HasMaxLength(Lengths.Address);
        builder.Property(x => x.PieceUnit).HasMaxLength(Lengths.Code);
        builder.Property(x => x.CompletionNote).HasMaxLength(Lengths.Address);
        builder.Property(x => x.CancellationReason).HasMaxLength(Lengths.Reason);
        builder.HasIndex(x => new { x.ZoneId, x.WorkDate });
        builder.HasIndex(x => x.CompletionDate);

        // Exactly one assignee (employee, worker or team); piecework carries its unit, plan and rate (E40, LAB-03).
        var oneAssignee = ConfigurationHelpers.ExactlyOneNotNull(
            nameof(WorkAssignment.EmployeeId), nameof(WorkAssignment.WorkerId), nameof(WorkAssignment.ContractorId));
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_WorkAssignment_PaymentType",
            $"{oneAssignee} AND ([PaymentType] = N'DailyWage' OR " +
            "([PaymentType] = N'Piecework' AND [PieceUnit] IS NOT NULL AND [PlannedQuantity] IS NOT NULL AND [PieceRate] IS NOT NULL))"));
    }
}
