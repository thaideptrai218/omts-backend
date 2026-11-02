using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OMTS.Domain.Entities.Sales;

namespace OMTS.Infrastructure.Persistence.Configurations.Sales;

internal sealed class PurchaseRequestConfiguration : IEntityTypeConfiguration<PurchaseRequest>
{
    public void Configure(EntityTypeBuilder<PurchaseRequest> b)
    {
        b.Property(x => x.Code).HasMaxLength(Lengths.Code);
        b.Property(x => x.ProductName).HasMaxLength(Lengths.Name);
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.ResponseNote).HasMaxLength(500);
        b.Property(x => x.CancellationReason).HasMaxLength(500);
        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
        b.HasOne(x => x.Status).WithMany().HasForeignKey(x => x.StatusId);
        b.ReferencesAccount(nameof(PurchaseRequest.RespondedByAccountId));
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.CustomerId, x.ClientOperationId }).IsUnique();
        b.HasIndex(x => new { x.StatusId, x.CreatedAt });
        b.HasIndex(x => new { x.CustomerId, x.CreatedAt });
        b.ToTable(t => t.HasCheckConstraint("CK_PurchaseRequest_Cages",
            "[RequestedCages] > 0 AND ([AcceptedCages] IS NULL OR ([AcceptedCages] > 0 AND [AcceptedCages] <= [RequestedCages]))"));
    }
}
