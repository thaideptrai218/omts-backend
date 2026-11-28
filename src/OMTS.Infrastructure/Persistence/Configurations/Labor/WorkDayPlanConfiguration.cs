using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.CashBook;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class WorkDayPlanConfiguration : IEntityTypeConfiguration<WorkDayPlan>
{
    public void Configure(EntityTypeBuilder<WorkDayPlan> b)
    {
        b.Property(x => x.Code).HasMaxLength(Lengths.Code);
        // Unique regardless of letter case: the column uses the case-insensitive collation of the database.
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        b.Property(x => x.ResponseReason).HasMaxLength(500);
        b.HasOne(x => x.CreatorAccount).WithMany().HasForeignKey(x => x.CreatorAccountId);
        b.HasOne(x => x.ResponsibleAccount).WithMany().HasForeignKey(x => x.ResponsibleAccountId);
        b.HasOne(x => x.Zone).WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        b.HasOne(x => x.NurseryBatch).WithMany().HasForeignKey(x => x.NurseryBatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.JobType).WithMany().HasForeignKey(x => x.JobTypeId);
        b.HasOne(x => x.PurchaseRequest).WithOne(x => x.WorkDayPlan).HasForeignKey<WorkDayPlan>(x => x.PurchaseRequestId);
        // A harvest plan belongs to one line; the plan stays (cancelled, with no line) when the line is taken out of the harvest plan: the service clears the link first.
        b.HasOne(x => x.HarvestPlanLine).WithOne(l => l.WorkDayPlan).HasForeignKey<WorkDayPlan>(x => x.HarvestPlanLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CreatorAccountId, x.ClientOperationId }).IsUnique();
        b.HasIndex(x => new { x.ResponsibleAccountId, x.WorkDate });
        b.HasIndex(x => new { x.ZoneId, x.WorkDate });
        b.HasIndex(x => new { x.WorkDate, x.Status });
        b.HasIndex(x => x.NurseryBatchId);
        b.ToTable("WorkDayPlans", t =>
        {
            t.HasCheckConstraint("CK_WorkDayPlan_Response",
                "([Approved] IS NULL AND [RespondedAt] IS NULL) OR ([Approved] IS NOT NULL AND [RespondedAt] IS NOT NULL AND ([Approved] = 1 OR ([ResponseReason] IS NOT NULL AND LEN(LTRIM(RTRIM([ResponseReason]))) > 0)))");
            t.HasCheckConstraint("CK_WorkDayPlan_Kind", "[Kind] IN ('Harvest', 'Nursery', 'Seeding')");
            t.HasCheckConstraint("CK_WorkDayPlan_Status", "[Status] IN ('Staffing', 'Ready', 'InProgress', 'Completed', 'Cancelled')");
            t.HasCheckConstraint("CK_WorkDayPlan_RequiredPeople", "[RequiredPeople] BETWEEN 1 AND 500");
            // One place: a raft for a harvest or a seeding plan, the zone alone for a nursery plan.
            t.HasCheckConstraint("CK_WorkDayPlan_Place", "([Kind] = 'Nursery' AND [RaftId] IS NULL) OR ([Kind] <> 'Nursery' AND [RaftId] IS NOT NULL)");
            t.HasCheckConstraint("CK_WorkDayPlan_HarvestLine", "[HarvestPlanLineId] IS NULL OR [Kind] = 'Harvest'");
        });
    }
}

internal sealed class WorkDayPlanMemberConfiguration : IEntityTypeConfiguration<WorkDayPlanMember>
{
    public void Configure(EntityTypeBuilder<WorkDayPlanMember> b)
    {
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne(x => x.Plan).WithMany(p => p.Members).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.ReferencesAccount(nameof(WorkDayPlanMember.AddedByAccountId), nameof(WorkDayPlanMember.ConfirmedByAccountId));
        b.Ignore(x => x.IsConfirmed);
        // One row per person per plan; the expiry job reads the invitations whose deadline has passed.
        b.HasIndex(x => new { x.PlanId, x.EmployeeId }).IsUnique();
        b.HasIndex(x => new { x.State, x.Deadline });
        b.HasIndex(x => new { x.EmployeeId, x.State });
        b.HasIndex(x => x.WorkAssignmentId);
        b.ToTable("WorkDayPlanMembers", t =>
        {
            t.HasCheckConstraint("CK_WorkDayPlanMember_State", "[State] IN ('Invited', 'Accepted', 'ConfirmedOnBehalf', 'Declined', 'Expired', 'Unsuccessful')");
            t.HasCheckConstraint("CK_WorkDayPlanMember_OnBehalf", "[State] <> 'ConfirmedOnBehalf' OR ([Reason] IS NOT NULL AND [ConfirmedByAccountId] IS NOT NULL)");
        });
    }
}

internal sealed class PlanOutsiderHireConfiguration : IEntityTypeConfiguration<PlanOutsiderHire>
{
    public void Configure(EntityTypeBuilder<PlanOutsiderHire> b)
    {
        b.Property(x => x.RatePerSession).HasPrecision(18, 0);
        b.Property(x => x.PayeeName).HasMaxLength(Lengths.Name);
        b.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        b.HasOne(x => x.Plan).WithMany(p => p.OutsiderHires).HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CashVoucher>().WithMany().HasForeignKey(x => x.CashVoucherId).OnDelete(DeleteBehavior.Restrict);
        b.ReferencesAccount(nameof(PlanOutsiderHire.PaidByAccountId));
        b.Ignore(x => x.Amount);
        b.Ignore(x => x.IsPaid);
        b.HasIndex(x => x.PlanId);
        b.HasIndex(x => x.CashVoucherId);
        b.ToTable("PlanOutsiderHires", t =>
        {
            t.HasCheckConstraint("CK_PlanOutsiderHire_People", "[People] BETWEEN 1 AND 500");
            t.HasCheckConstraint("CK_PlanOutsiderHire_Rate", "[RatePerSession] >= 0");
            t.HasCheckConstraint("CK_PlanOutsiderHire_Paid", "([CashVoucherId] IS NULL AND [PaidAt] IS NULL) OR ([CashVoucherId] IS NOT NULL AND [PaidAt] IS NOT NULL)");
        });
    }
}
