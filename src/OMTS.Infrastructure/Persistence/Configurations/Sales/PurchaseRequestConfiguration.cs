using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Constants;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PurchaseRequestConfiguration : IEntityTypeConfiguration<PurchaseRequest>
{
    public void Configure(EntityTypeBuilder<PurchaseRequest> builder)
    {
        builder.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.ReferencesAccount(nameof(PurchaseRequest.RespondedByAccountId));
        builder.Property(x => x.RequestedKg).HasPrecision(18, 3);
        builder.Property(x => x.AcceptedKg).HasPrecision(18, 3);
        builder.Property(x => x.Note).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.ResponseNote).HasMaxLength(Lengths.Reason);
        builder.Property(x => x.CancelReason).HasMaxLength(Lengths.Reason);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PurchaseRequest_RequestedKg_Positive", "[RequestedKg] > 0");
            t.HasCheckConstraint("CK_PurchaseRequest_AcceptedKg_Range", "[AcceptedKg] IS NULL OR ([AcceptedKg] > 0 AND [AcceptedKg] <= [RequestedKg])");
            t.HasCheckConstraint(
                "CK_PurchaseRequest_Accepted_Has_Response",
                $"[StatusId] <> {StatusIds.PurchaseRequest.Accepted} OR ([AcceptedKg] IS NOT NULL AND [AgreedPickupDate] IS NOT NULL AND [PayLaterAgreed] IS NOT NULL)");
            t.HasCheckConstraint("CK_PurchaseRequest_Declined_Has_Note", $"[StatusId] <> {StatusIds.PurchaseRequest.Declined} OR [ResponseNote] IS NOT NULL");
        });

        // SCR-SALE-01: the requests of a profile, newest first. SCR-SALE-02: those Awaiting response first.
        builder.HasIndex(x => new { x.CustomerId, x.SentAt });
        builder.HasIndex(x => new { x.StatusId, x.SentAt });
    }
}
