using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Labor;
namespace OMTS.Infrastructure.Persistence.Configurations.Labor;

internal sealed class EmployeeLeaveRequestConfiguration : IEntityTypeConfiguration<EmployeeLeaveRequest>
{
    public void Configure(EntityTypeBuilder<EmployeeLeaveRequest> builder)
    {
        builder.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        builder.ReferencesAccount(nameof(EmployeeLeaveRequest.ReviewedByAccountId));
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.Response).HasMaxLength(500);
        builder.HasIndex(x => new { x.EmployeeId, x.ClientOperationId }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_EmployeeLeaveRequest_Dates", "[To] >= [From]"));
    }
}
