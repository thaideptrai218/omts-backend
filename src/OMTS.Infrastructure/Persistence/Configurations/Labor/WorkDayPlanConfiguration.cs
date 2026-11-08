using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class WorkDayPlanConfiguration : IEntityTypeConfiguration<WorkDayPlan>
{
    public void Configure(EntityTypeBuilder<WorkDayPlan> b)
    {
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.ResponseReason).HasMaxLength(500);
        b.HasOne(x => x.CreatorAccount).WithMany().HasForeignKey(x => x.CreatorAccountId);
        b.HasOne(x => x.ResponsibleAccount).WithMany().HasForeignKey(x => x.ResponsibleAccountId);
        b.HasOne(x => x.Raft).WithMany().HasForeignKey(x => x.RaftId);
        b.HasOne(x => x.JobType).WithMany().HasForeignKey(x => x.JobTypeId);
        b.HasOne(x => x.PurchaseRequest).WithOne(x => x.WorkDayPlan).HasForeignKey<WorkDayPlan>(x => x.PurchaseRequestId);
        b.HasIndex(x => new { x.CreatorAccountId, x.ClientOperationId }).IsUnique();
        b.HasIndex(x => new { x.ResponsibleAccountId, x.WorkDate });
        b.ToTable(t => t.HasCheckConstraint("CK_WorkDayPlan_Response",
            "([Approved] IS NULL AND [RespondedAt] IS NULL) OR ([Approved] IS NOT NULL AND [RespondedAt] IS NOT NULL AND ([Approved] = 1 OR ([ResponseReason] IS NOT NULL AND LEN(LTRIM(RTRIM([ResponseReason]))) > 0)))"));
    }
}
