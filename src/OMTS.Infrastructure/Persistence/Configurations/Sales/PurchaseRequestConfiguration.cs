using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PurchaseRequestConfiguration : IEntityTypeConfiguration<PurchaseRequest>
{
    public void Configure(EntityTypeBuilder<PurchaseRequest> b)
    {
        b.Property(x => x.RequestedKg).HasPrecision(18, 3);
        b.Property(x => x.AcceptedKg).HasPrecision(18, 3);
        b.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        b.HasIndex(x => new { x.CustomerId, x.SentAt });
        b.HasIndex(x => new { x.StatusId, x.SentAt });
        b.Property(x => x.Code).HasMaxLength(Lengths.Code);
        b.Property(x => x.ProductName).HasMaxLength(Lengths.Name);
        b.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        b.Property(x => x.ResponseNote).HasMaxLength(Lengths.Reason);
        b.Property(x => x.CancellationReason).HasMaxLength(Lengths.Reason);
        b.Property(x => x.StaffingNote).HasMaxLength(500);
        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        b.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        b.ReferencesAccount(nameof(PurchaseRequest.RespondedByAccountId));
        b.HasIndex(x => x.Code).IsUnique().HasFilter("[Code] <> N''");
        b.HasIndex(x => new { x.CustomerId, x.ClientOperationId }).IsUnique().HasFilter("[ClientOperationId] <> '00000000-0000-0000-0000-000000000000'");
        b.HasIndex(x => new { x.StatusId, x.CreatedAt });
        b.HasIndex(x => new { x.CustomerId, x.CreatedAt });
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PurchaseRequest_RequestedKg_Positive", "[RequestedCages] > 0 OR [RequestedKg] > 0");
            t.HasCheckConstraint("CK_PurchaseRequest_AcceptedKg_Range", "[AcceptedKg] IS NULL OR ([RequestedCages] = 0 AND [AcceptedKg] > 0 AND [AcceptedKg] <= [RequestedKg])");
            t.HasCheckConstraint("CK_PurchaseRequest_Accepted_Has_Response",
                $"[StatusId] <> {StatusIds.PurchaseRequest.Accepted} OR " +
                "([RequestedCages] > 0 AND [AcceptedCages] IS NOT NULL AND [AcceptedPickupDate] IS NOT NULL) OR " +
                "([RequestedCages] = 0 AND [AcceptedKg] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");
            t.HasCheckConstraint("CK_PurchaseRequest_Declined_Has_Note", $"[StatusId] <> {StatusIds.PurchaseRequest.Declined} OR [ResponseNote] IS NOT NULL");
        });
        b.ToTable(t => t.HasCheckConstraint("CK_PurchaseRequest_Cages",
            "(([RequestedCages] > 0 AND [RequestedKg] = 0) OR ([RequestedCages] = 0 AND [RequestedKg] > 0)) AND ([AcceptedCages] IS NULL OR ([AcceptedCages] > 0 AND [AcceptedCages] <= [RequestedCages]))"));
        b.ToTable(t => t.HasCheckConstraint("CK_PurchaseRequest_Staffing",
            "([PlannedCages] IS NULL AND [WorkDate] IS NULL AND [PlannedPickupDate] IS NULL AND [CagesPerEmployeePerDay] IS NULL) OR " +
            "([PlannedCages] IS NOT NULL AND [PlannedCages] > 0 AND [PlannedCages] <= [RequestedCages] AND " +
            "[WorkDate] IS NOT NULL AND [PlannedPickupDate] IS NOT NULL AND [WorkDate] <= [PlannedPickupDate] AND " +
            "[CagesPerEmployeePerDay] IS NOT NULL AND [CagesPerEmployeePerDay] > 0)"));
    }
}
