using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Labor;

namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeRequestConfiguration : IEntityTypeConfiguration<EmployeeRequest>
{
    public void Configure(EntityTypeBuilder<EmployeeRequest> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.ReferencesAccount(nameof(EmployeeRequest.ReviewedByAccountId));
        builder.Property(x => x.Reason).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ReviewNote).HasMaxLength(Lengths.Reason);

        // One submit of the client is saved once (a retry returns it), and an employee has at most one request Awaiting review for the same
        // day and missed time (BR-LABOR-08); overlapping leave is checked by the service. Parallel saves cannot both pass the pre-check.
        builder.HasIndex(x => new { x.EmployeeId, x.ClientOperationId })
            .IsUnique()
            .HasDatabaseName("UX_EmployeeRequests_Employee_ClientOperation")
            .HasFilter("[ClientOperationId] IS NOT NULL");
        builder.HasIndex(x => new { x.EmployeeId, x.Type, x.FromDate })
            .IsUnique()
            .HasDatabaseName("UX_EmployeeRequests_Awaiting_MissedTime")
            .HasFilter($"[StatusId] = {StatusIds.EmployeeRequest.AwaitingReview} AND [Type] <> N'Leave'");
        builder.HasIndex(x => new { x.StatusId, x.SentAt });
        builder.HasIndex(x => new { x.EmployeeId, x.FromDate });
        builder.HasIndex(x => x.ReviewedByAccountId);
        builder.ToTable(t => t.HasCheckConstraint("CK_EmployeeRequests_Period", "[ToDate] >= [FromDate]"));
    }
}
