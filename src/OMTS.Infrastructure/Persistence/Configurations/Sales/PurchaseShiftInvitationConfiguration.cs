using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PurchaseShiftInvitationConfiguration : IEntityTypeConfiguration<PurchaseShiftInvitation>
{
    public void Configure(EntityTypeBuilder<PurchaseShiftInvitation> b)
    {
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne(x => x.PurchaseRequest).WithMany(x => x.ShiftInvitations).HasForeignKey(x => x.PurchaseRequestId);
        b.HasOne(x => x.Employee).WithMany().HasForeignKey(x => x.EmployeeId);
        b.HasIndex(x => new { x.PurchaseRequestId, x.EmployeeId }).IsUnique();
        b.HasIndex(x => new { x.EmployeeId, x.ReservedWorkDate }).IsUnique().HasFilter("[ReservedWorkDate] IS NOT NULL");
        b.ToTable("PurchaseShiftInvitations", t => t.HasCheckConstraint("CK_PurchaseShiftInvitation_Reservation",
            "[ReservedWorkDate] IS NULL OR ([Approved] IS NOT NULL AND [Approved] = 1 AND [RespondedAt] IS NOT NULL)"));
    }
}
